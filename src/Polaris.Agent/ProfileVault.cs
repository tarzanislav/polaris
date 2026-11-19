using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>The trezor of profiles on this PC: for each shared account its prepared profile in one archive (only the
/// system and administrators can read it) and its description. Saved when nobody is signed in to the account; after a
/// sign-out only the differences are put back, while the account is locked for a moment so nobody signs in to a profile
/// half put back. Whatever goes wrong, the account is never left locked and the profile never left half – it is then
/// deleted as before (a clean start), and the journal says so.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class ProfileVault(string? folder = null, string stateKey = ProfileVault.DefaultStateKey)
{
    public const string DefaultStateKey = @"SOFTWARE\POLARIS\Agent\Trezor";

    private const string RestoredValue = "Obnovene";
    private const string LockedValue = "Zablokovane";
    private const string PreparingValue = "Priprava";

    private static readonly Dictionary<string, (DateTime Stamp, VaultManifest? Manifest)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public string Folder { get; } = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Polaris.Core.Product.Name, "agent", "trezor");

    public sealed record Saved(int Entries, long Bytes, double Seconds);

    public sealed record Restored(int Removed, int Put, double Seconds, string? Note = null);

    public string AccountFolder(string account) => Path.Combine(Folder, account.ToLowerInvariant());

    /// <summary>The description of the account's trezor, or null when it has none (or a broken one).</summary>
    public VaultManifest? Manifest(string account)
    {
        var file = Path.Combine(AccountFolder(account), VaultManifest.FileName);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            // the service looks every 20 seconds: a description read once is kept until its file changes
            var stamp = File.GetLastWriteTimeUtc(file);
            lock (Cache)
            {
                if (Cache.TryGetValue(file, out var known) && known.Stamp == stamp)
                {
                    return known.Manifest;
                }
            }

            var m = VaultRules.FromJson(File.ReadAllText(file, Encoding.UTF8));
            var fit = VaultRules.Problems(m).Any() ? null : m;
            lock (Cache)
            {
                Cache[file] = (stamp, fit);
            }

            return fit;
        }
        catch (Exception e) when (e is InvalidDataException or Polaris.Json.JsonError or IOException)
        {
            return null;
        }
    }

    /// <summary>Why the trezor cannot be used for the profile as it is now, or null when it fits.</summary>
    public string? Unfit(string account, string sid)
    {
        if (Manifest(account) is not { } m)
        {
            return "konto nemá trezor";
        }

        if (!string.Equals(m.Sid, sid, StringComparison.OrdinalIgnoreCase))
        {
            return "trezor je z iného konta s tým istým menom – ulož ho znova";
        }

        if (ProfileFolder(sid) is not { } folder || Directory.GetCreationTimeUtc(folder).ToFileTimeUtc() != m.ProfileCreated)
        {
            return "profil konta vznikol znova po uložení trezoru – ulož ho znova";
        }

        var archive = Path.Combine(AccountFolder(account), VaultManifest.ArchiveName);
        return !File.Exists(archive) || new FileInfo(archive).Length != m.ArchiveLength ? "archív trezoru chýba alebo nesedí" : null;
    }

    /// <summary>Keeps the profile of the account as it is now. Nobody may be signed in to it.</summary>
    public Saved Save(string account, Func<DateTimeOffset> clock)
    {
        var sid = Profiles.SidOf(account) ?? throw new InvalidOperationException($"Konto {account} neexistuje.");
        if (SessionPolicy.NeverClean.Contains(account) || Profiles.IsAdministrator(account))
        {
            throw new InvalidOperationException($"Konto {account} je správca alebo systémové – trezor je len pre zdieľané kontá žiakov.");
        }

        var state = Profiles.State(sid);
        var profile = ProfileFolder(sid);
        if (!state.Exists || profile is null || !Directory.Exists(profile))
        {
            throw new InvalidOperationException($"Konto {account} ešte nemá profil – prihlás sa doň, priprav ho a odhlás sa.");
        }

        if (state.Loaded)
        {
            throw new InvalidOperationException($"Na konte {account} je niekto prihlásený (alebo Windows ešte drží jeho profil) – odhlás sa a skús to o chvíľu.");
        }

        var watch = Stopwatch.StartNew();
        var target = AccountFolder(account);
        Directory.CreateDirectory(target);
        AgentService.ProtectFolder(Folder);
        var archive = Path.Combine(target, VaultManifest.ArchiveName);
        var description = Path.Combine(target, VaultManifest.FileName);
        var items = ProfileArchive.Read(profile, skipCaches: true);
        List<VaultEntry> entries;
        long length;
        using (var stream = new FileStream(archive + ".novy", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            entries = ProfileArchive.Save(profile, items, stream);
            stream.Flush(flushToDisk: true);
            length = stream.Length;
        }

        var manifest = new VaultManifest
        {
            Account = account,
            Sid = sid,
            ProfileCreated = Directory.GetCreationTimeUtc(profile).ToFileTimeUtc(),
            Windows = WindowsBuild(),
            SavedUtc = clock(),
            ArchiveLength = length,
            Entries = entries,
        };
        File.WriteAllText(description + ".novy", VaultRules.ToJson(manifest), new UTF8Encoding(false));

        // the description goes last: a trezor whose description is older than its archive does not fit and is not used
        File.Delete(description);
        File.Move(archive + ".novy", archive, overwrite: true);
        File.Move(description + ".novy", description);
        MarkRestored(account, sid);
        SetPreparing(account, false);
        return new Saved(entries.Count, length, watch.Elapsed.TotalSeconds);
    }

    /// <summary>The content of files in the trezor of the account (each checked by its fingerprint): the files whose path
    /// the filter takes, small ones only.</summary>
    public List<(string Path, byte[] Data)> Files(string account, Func<string, bool> which, long largest)
    {
        var manifest = Manifest(account) ?? throw new InvalidOperationException($"Konto {account} nemá trezor (polaris-agent trezor ulozit {account}).");
        var found = new List<(string, byte[])>();
        var wanted = manifest.Entries.Where(e => e.Kind == VaultKind.File && which(e.Path)).ToList();
        if (wanted.Count == 0)
        {
            return found;
        }

        using var archive = new FileStream(Path.Combine(AccountFolder(account), VaultManifest.ArchiveName), FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var e in wanted)
        {
            if (e.Length > largest || e.Offset < 0 || e.Offset + e.Length > archive.Length)
            {
                throw new InvalidDataException($"Súbor {e.Path} v trezore je príliš veľký alebo mimo archívu.");
            }

            var record = new byte[e.Length];
            archive.Position = e.Offset;
            archive.ReadExactly(record);
            if (Convert.ToHexStringLower(SHA256.HashData(record)) != e.Sha256)
            {
                throw new InvalidDataException($"Súbor {e.Path} v trezore nesedí s odtlačkom.");
            }

            found.Add((e.Path, BackupStreams.FileData(record)));
        }

        return found;
    }

    /// <summary>The admin prepares the profile: it is not cleaned until it is saved to the trezor.</summary>
    public bool Preparing(string account)
    {
        using var key = Registry.LocalMachine.OpenSubKey(stateKey + "\\" + account);
        return key?.GetValue(PreparingValue) is not null;
    }

    public void SetPreparing(string account, bool on)
    {
        using var key = Registry.LocalMachine.CreateSubKey(stateKey + "\\" + account);
        if (on)
        {
            key.SetValue(PreparingValue, 1, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(PreparingValue, throwOnMissingValue: false);
        }
    }

    /// <summary>The profile was put back after its last sign-in already (nothing to do).</summary>
    public bool UpToDate(string account, string sid)
    {
        using var key = Registry.LocalMachine.OpenSubKey(stateKey + "\\" + account);
        return key?.GetValue(RestoredValue) is string done && done == LoadTime(sid).ToString(CultureInfo.InvariantCulture) && key.GetValue(LockedValue) is null;
    }

    /// <summary>Puts back what differs from the trezor. The account is locked meanwhile; on a failure the profile is
    /// deleted (a clean start) and the failure thrown on.</summary>
    public Restored Restore(string account, string sid)
    {
        var manifest = Manifest(account) ?? throw new InvalidOperationException("Konto nemá trezor.");
        var profile = ProfileFolder(sid) ?? throw new InvalidOperationException("Konto nemá profil.");
        var watch = Stopwatch.StartNew();
        Lock(account);
        try
        {
            var plan = VaultRules.Plan(ProfileArchive.Read(profile, skipCaches: false), manifest);
            using (var archive = new FileStream(Path.Combine(AccountFolder(account), VaultManifest.ArchiveName), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                ProfileArchive.Apply(profile, plan, archive);
            }

            // what recipes gave the profiles since the trezor was saved, before anybody signs in again
            var note = ProfileValues.AfterRestore(profile);
            MarkRestored(account, sid);
            return new Restored(plan.Remove.Count, plan.Put.Count, watch.Elapsed.TotalSeconds, note);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // never a half profile: the next sign-in starts clean, as without a trezor
            try
            {
                Profiles.Delete(sid);
            }
            catch (SystemCallException)
            {
                // reported with the first failure
            }

            throw new InvalidOperationException("obnova z trezoru zlyhala (" + e.Message + "), profil zmazaný – ďalšie prihlásenie začne načisto", e);
        }
        finally
        {
            Unlock(account);
        }
    }

    /// <summary>An account left locked (power lost during a restore) is unlocked.</summary>
    public void UnlockLeftovers()
    {
        using var root = Registry.LocalMachine.OpenSubKey(stateKey);
        foreach (var account in root?.GetSubKeyNames() ?? [])
        {
            Unlock(account);
        }
    }

    public void Forget(string account)
    {
        var folder = AccountFolder(account);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Registry.LocalMachine.DeleteSubKeyTree(stateKey + "\\" + account, throwOnMissingSubKey: false);
    }

    public static string WindowsBuild()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return $"{key?.GetValue("CurrentBuild")}.{key?.GetValue("UBR")}";
    }

    public static string? ProfileFolder(string sid)
    {
        using var entry = Registry.LocalMachine.OpenSubKey(Profiles.ProfileListKey + "\\" + sid);
        return entry?.GetValue("ProfileImagePath") is string path ? Environment.ExpandEnvironmentVariables(path) : null;
    }

    /// <summary>When Windows last loaded the profile (changes with every sign-in), 0 when unknown.</summary>
    private static long LoadTime(string sid)
    {
        using var entry = Registry.LocalMachine.OpenSubKey(Profiles.ProfileListKey + "\\" + sid);
        return entry?.GetValue("LocalProfileLoadTimeHigh") is int high && entry.GetValue("LocalProfileLoadTimeLow") is int low
            ? ((long)(uint)high << 32) | (uint)low
            : 0;
    }

    private void MarkRestored(string account, string sid)
    {
        using var key = Registry.LocalMachine.CreateSubKey(stateKey + "\\" + account);
        key.SetValue(RestoredValue, LoadTime(sid).ToString(CultureInfo.InvariantCulture), RegistryValueKind.String);
    }

    /// <summary>Locks the account (its flags are kept first, so they come back even after a power loss).</summary>
    private void Lock(string account)
    {
        using var key = Registry.LocalMachine.CreateSubKey(stateKey + "\\" + account);
        if (key.GetValue(LockedValue) is not int)
        {
            key.SetValue(LockedValue, (int)Accounts.Flags(account), RegistryValueKind.DWord);
        }

        Accounts.SetFlags(account, (uint)(int)key.GetValue(LockedValue)! | Accounts.Disabled);
    }

    private void Unlock(string account)
    {
        using var key = Registry.LocalMachine.OpenSubKey(stateKey + "\\" + account, writable: true);
        if (key?.GetValue(LockedValue) is int flags)
        {
            Accounts.SetFlags(account, (uint)flags);
            key.DeleteValue(LockedValue);
        }
    }
}

/// <summary>Flags of a local account (disabled, password cannot change …), through the network management of Windows.</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class Accounts
{
    public const uint Disabled = 0x2;
    public const uint PasswordCannotChange = 0x40;
    public const uint PasswordNotRequired = 0x20;
    public const uint PasswordNeverExpires = 0x10000;

    public const uint Script = 0x1;

    /// <summary>A new standard account (member of Users) with a random password nobody knows (a password policy may refuse
    /// an empty one at creation); the empty password is set after, once the account does not require one.</summary>
    public static void Add(string account, uint flags)
    {
        var name = Marshal.StringToHGlobalUni(account);
        var password = Marshal.StringToHGlobalUni("Pa1-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)) + "-x");
        try
        {
            var info = new UserInfo1 { Name = name, Password = password, Privilege = 1 /* USER_PRIV_USER */, Flags = flags | Script };
            var status = NetUserAdd(null, 1, &info, out _);
            if (status != 0)
            {
                throw new SystemCallException("Vytvorenie konta " + account, status);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(password);
        }
    }

    public static void SetPassword(string account, string password)
    {
        var text = Marshal.StringToHGlobalUni(password);
        try
        {
            var status = NetUserSetInfo(null, account, 1003, &text, out _);
            if (status != 0)
            {
                throw new SystemCallException("Heslo konta " + account, status);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    /// <summary>Seconds since the password of the account last changed.</summary>
    public static uint PasswordAgeSeconds(string account)
    {
        var status = NetUserGetInfo(null, account, 1, out var buffer);
        if (status != 0)
        {
            throw new SystemCallException("Vlastnosti konta " + account, status);
        }

        try
        {
            return ((UserInfo1*)buffer)->PasswordAge;
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo1
    {
        public nint Name;
        public nint Password;
        public uint PasswordAge;
        public uint Privilege;
        public nint Home;
        public nint Comment;
        public uint Flags;
        public nint Script;
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetUserAdd(string? server, int level, void* buffer, out int error);

    public static uint Flags(string account)
    {
        var status = NetUserGetInfo(null, account, 1, out var buffer);
        if (status != 0)
        {
            throw new SystemCallException("Vlastnosti konta " + account, status);
        }

        try
        {
            return ((UserInfo1*)buffer)->Flags;
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    public static void SetFlags(string account, uint flags)
    {
        var status = NetUserSetInfo(null, account, 1008, &flags, out _);
        if (status != 0)
        {
            throw new SystemCallException("Zmena konta " + account, status);
        }
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetUserGetInfo(string? server, string user, int level, out nint buffer);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetUserSetInfo(string? server, string user, int level, void* buffer, out int error);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(nint buffer);
}
