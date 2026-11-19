using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>Values written into the template of profiles (the profile "Default"): every new profile gets them – the pupil's
/// after every cleaning too. A profile that already exists – of a shared account, the pupil's from the trezor – gets them
/// as well (ProfileValues), and the agent keeps them, to write them again after every return from the trezor. The
/// registry file of the template is opened privately for this process (an application hive, no special privileges,
/// nothing mounted for others) once for a run, and closed at its end.</summary>
[SupportedOSPlatform("windows")]
public sealed class ProfileTemplateHandler(string? hiveFile = null, bool? existingProfiles = null) : IElementHandler, IDisposable
{
    /// <summary>Existing profiles too – by default only with the real template (a test with a copy touches nothing else).</summary>
    private readonly bool _existing = existingProfiles ?? hiveFile is null;

    private RegistryKey? _root;

    public bool CanHandle(Element element) => element is UserRegistryElement or SoundsElement;

    public Observation Observe(Element element)
    {
        var template = ProfileValues.Read(Root(), element);
        if (!_existing || !ProfileValueFormat.Matches(element, template))
        {
            return template;
        }

        if (!ProfileValues.Remembers(element))
        {
            return new Observation(Observed.Different, "predloha áno; agent si ju ešte nedrží pre profily z trezoru");
        }

        foreach (var (account, sid) in ProfileValues.Managed())
        {
            try
            {
                var found = ProfileValues.InProfile(sid, root => ProfileValues.Read(root, element));
                if (found is not null && !ProfileValueFormat.Matches(element, found))
                {
                    return found with { Detail = $"v profile konta {account}: {found.Detail}".TrimEnd(' ', ':') };
                }
            }
            catch (SystemCallException)
            {
                // a profile that cannot be opened now (just signing in): written after its next return from the trezor
            }
        }

        return template;
    }

    public void Apply(Element element)
    {
        var root = Root();
        ProfileValues.Write(root, element);
        root.Flush();
        if (!_existing)
        {
            return;
        }

        ProfileValues.Remember(element);
        var failed = new List<string>();
        foreach (var (account, sid) in ProfileValues.Managed())
        {
            try
            {
                ProfileValues.InProfile(sid, r =>
                {
                    ProfileValues.Write(r, element);
                    r.Flush();
                    return true;
                });
            }
            catch (Exception e) when (e is SystemCallException or IOException or UnauthorizedAccessException)
            {
                failed.Add($"{account} ({e.Message})");
            }
        }

        if (failed.Count > 0)
        {
            throw new InvalidOperationException("Predloha profilov áno, existujúci profil nie: " + string.Join(", ", failed)
                + ". Hodnotu dostane po najbližšom návrate z trezoru.");
        }
    }

    public void Dispose()
    {
        _root?.Dispose(); // closing the last handle unloads the file
        _root = null;
    }

    /// <summary>NTUSER.DAT of the profile "Default", as Windows says where it is.</summary>
    public static string DefaultHiveFile()
    {
        using var list = Registry.LocalMachine.OpenSubKey(Profiles.ProfileListKey)
            ?? throw new InvalidOperationException("Zoznam profilov Windows sa nedá otvoriť.");
        var folder = list.GetValue("Default") as string ?? throw new InvalidOperationException("Windows neuvádza predlohu profilov (Default).");
        return Path.Combine(Environment.ExpandEnvironmentVariables(folder), "NTUSER.DAT");
    }

    private RegistryKey Root() => _root ??= ProfileValues.OpenHive(hiveFile ?? DefaultHiveFile(), "predlohy profilov");
}

/// <summary>Values of profiles in existing profiles: the shared accounts whose sessions POLARIS cleans or returns from the
/// trezor (never an administrator's). What a recipe gave is kept in HKLM\SOFTWARE\POLARIS\Agent\HodnotyProfilov (one value
/// a setting) and written again into a profile just returned from the trezor, before anybody signs in.</summary>
[SupportedOSPlatform("windows")]
public static partial class ProfileValues
{
    public const string StoreKey = @"SOFTWARE\POLARIS\Agent\HodnotyProfilov";

    private const string Schemes = @"AppEvents\Schemes";

    public static Observation Read(RegistryKey root, Element element)
    {
        if (element is SoundsElement)
        {
            var loud = Sounding(root).Count;
            return loud == 0 && SchemeName(root) == ".None"
                ? new Observation(Observed.Present, "bez zvukov")
                : new Observation(Observed.Different, $"zvuková schéma {SchemeName(root) ?? "(nenastavená)"}, udalostí so zvukom: {loud}");
        }

        var u = (UserRegistryElement)element;
        return RegistryValues.Observe(root, UserRegistryPath.Normalize(u.Path), u.Name, u.Present, u.Kind, u.Data);
    }

    public static void Write(RegistryKey root, Element element)
    {
        if (element is SoundsElement sounds)
        {
            if (!sounds.Present)
            {
                return; // the scheme is then not managed: what the profile has stays
            }

            // what the dialog "Zvuk" does when the scheme "Bez zvukov" is chosen: the scheme's name and every event silent
            using (var schemes = root.CreateSubKey(Schemes))
            {
                schemes.SetValue("", ".None", RegistryValueKind.String);
            }

            foreach (var current in Sounding(root))
            {
                using var key = root.OpenSubKey(current, writable: true);
                key?.SetValue("", "", RegistryValueKind.String);
            }

            return;
        }

        var u = (UserRegistryElement)element;
        RegistryValues.Apply(root, UserRegistryPath.Normalize(u.Path), u.Name, u.Present, u.Kind, u.Data);
    }

    /// <summary>The value is kept for the profiles, as the element has it now.</summary>
    public static void Remember(Element element)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(StoreKey, writable: true);
        key.SetValue(element.Key, ProfileValueFormat.ToJson(element), RegistryValueKind.String);
    }

    public static bool Remembers(Element element)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(StoreKey);
        return key?.GetValue(element.Key) is string json && json == ProfileValueFormat.ToJson(element);
    }

    /// <summary>The kept values (an unreadable one is skipped – it never stops the return of a profile).</summary>
    public static List<Element> Remembered()
    {
        var found = new List<Element>();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(StoreKey);
        foreach (var name in key?.GetValueNames() ?? [])
        {
            if (key!.GetValue(name) is string json)
            {
                try
                {
                    found.Add(ProfileValueFormat.FromJson(json));
                }
                catch (InvalidDataException)
                {
                }
            }
        }

        return found;
    }

    /// <summary>The shared accounts with an existing profile that keep it for a while: returned from the trezor, or
    /// cleaned only after the day or the week (a profile cleaned at sign-out waits for deletion).</summary>
    public static List<(string Account, string Sid)> Managed()
    {
        var found = new List<(string, string)>();
        foreach (var (account, mode) in SessionRules.Read())
        {
            if (SessionPolicy.NeverClean.Contains(account) || Profiles.SidOf(account) is not { } sid)
            {
                continue;
            }

            var state = Profiles.State(sid);
            if (!state.Exists || (mode == SessionCleaning.AtSignOut && !state.Loaded) || Profiles.IsAdministrator(account))
            {
                continue;
            }

            found.Add((account, sid));
        }

        return found;
    }

    /// <summary>Work in the registry of a profile: the loaded one while its user is signed in, else its file opened
    /// privately for a moment. Null when the profile has no registry file.</summary>
    public static T? InProfile<T>(string sid, Func<RegistryKey, T> work)
    {
        using (var loaded = Registry.Users.OpenSubKey(sid, writable: true))
        {
            if (loaded is not null)
            {
                return work(loaded);
            }
        }

        var folder = ProfileVault.ProfileFolder(sid);
        var file = folder is null ? null : Path.Combine(folder, "NTUSER.DAT");
        if (file is null || !File.Exists(file))
        {
            return default;
        }

        using var hive = OpenHive(file, "profilu");
        return work(hive);
    }

    /// <summary>After a return from the trezor (the account still locked): every kept value into the profile's registry.
    /// Null when all went in, else what failed (the profile stays – a value is never a reason to delete it).</summary>
    public static string? AfterRestore(string profileFolder, IReadOnlyList<Element>? kept = null)
    {
        var values = kept ?? Remembered();
        var file = Path.Combine(profileFolder, "NTUSER.DAT");
        if (values.Count == 0 || !File.Exists(file))
        {
            return null;
        }

        try
        {
            using var hive = OpenHive(file, "profilu");
            var failed = 0;
            foreach (var value in values)
            {
                try
                {
                    Write(hive, value);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    failed++;
                }
            }

            hive.Flush();
            return failed == 0 ? null : $"hodnoty receptu: {failed} z {values.Count} sa nezapísali";
        }
        catch (SystemCallException e)
        {
            return "hodnoty receptu sa nezapísali: " + e.Message;
        }
    }

    /// <summary>A registry file opened only for this process (an application hive); closing the key closes the file.</summary>
    public static RegistryKey OpenHive(string file, string what)
    {
        var status = RegLoadAppKey(file, out var handle, 0xF003F /* KEY_ALL_ACCESS */, 0x1 /* REG_PROCESS_APPKEY */, 0);
        if (status != 0)
        {
            handle.Dispose();
            throw new SystemCallException($"Otvorenie registra {what} {file} (nemá ho otvorený iný program?)", status);
        }

        return RegistryKey.FromHandle(handle);
    }

    private static string? SchemeName(RegistryKey root)
    {
        using var schemes = root.OpenSubKey(Schemes);
        return schemes?.GetValue("") as string;
    }

    /// <summary>The ".Current" keys of events that still have a sound (AppEvents\Schemes\Apps\APP\EVENT\.Current).</summary>
    private static List<string> Sounding(RegistryKey root)
    {
        var found = new List<string>();
        using var apps = root.OpenSubKey(Schemes + @"\Apps");
        foreach (var app in apps?.GetSubKeyNames() ?? [])
        {
            using var appKey = apps!.OpenSubKey(app);
            foreach (var ev in appKey?.GetSubKeyNames() ?? [])
            {
                var path = $@"{Schemes}\Apps\{app}\{ev}\.Current";
                using var current = root.OpenSubKey(path);
                if (current?.GetValue("") is string { Length: > 0 })
                {
                    found.Add(path);
                }
            }
        }

        return found;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegLoadAppKeyW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegLoadAppKey(string file, out Microsoft.Win32.SafeHandles.SafeRegistryHandle key, uint access, uint options, uint reserved);
}

/// <summary>Accounts and their profiles: who is an administrator, whether a profile exists, is loaded, when it was used, and
/// deleting it through Windows.</summary>
[SupportedOSPlatform("windows")]
public static partial class Profiles
{
    public const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public sealed record ProfileState(string Sid, bool Exists, bool Loaded, DateTime? LastUsed);

    /// <summary>The SID of a local account, or null when there is no such account.</summary>
    public static string? SidOf(string account)
    {
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, account).Translate(typeof(SecurityIdentifier));
            return sid.Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    public static ProfileState State(string sid)
    {
        var loaded = false;
        using (var users = Registry.Users.OpenSubKey(sid))
        {
            loaded = users is not null;
        }

        using var entry = Registry.LocalMachine.OpenSubKey(ProfileListKey + "\\" + sid);
        if (entry is null)
        {
            return new ProfileState(sid, false, loaded, null);
        }

        DateTime? lastUsed = null;
        if (entry.GetValue("LocalProfileLoadTimeHigh") is int high && entry.GetValue("LocalProfileLoadTimeLow") is int low)
        {
            var fileTime = ((long)(uint)high << 32) | (uint)low;
            if (fileTime > 0)
            {
                lastUsed = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
            }
        }

        if (lastUsed is null && entry.GetValue("ProfileImagePath") is string folder)
        {
            var hive = Path.Combine(Environment.ExpandEnvironmentVariables(folder), "NTUSER.DAT");
            lastUsed = File.Exists(hive) ? File.GetLastWriteTime(hive) : null;
        }

        return new ProfileState(sid, true, loaded, lastUsed);
    }

    /// <summary>Whether the account is a member of the local Administrators (also through another group).</summary>
    public static bool IsAdministrator(string account)
    {
        var admins = ((NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount))).Value;
        var adminsName = admins[(admins.IndexOf('\\') + 1)..];
        var status = NetUserGetLocalGroups(null, account, 0, 1 /* LG_INCLUDE_INDIRECT */, out var buffer, -1, out var read, out _);
        if (status != 0)
        {
            throw new SystemCallException($"Skupiny konta {account}", status);
        }

        try
        {
            for (var i = 0; i < read; i++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
                if (string.Equals(name, adminsName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    public static void Delete(string sid)
    {
        if (!DeleteProfile(sid, null, null))
        {
            throw new SystemCallException("Zmazanie profilu " + sid, Marshal.GetLastPInvokeError());
        }
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetUserGetLocalGroups(string? server, string user, int level, int flags, out IntPtr buffer, int maxLength, out int read, out int total);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);

    [LibraryImport("userenv.dll", EntryPoint = "DeleteProfileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteProfile(string sid, string? profilePath, string? computerName);
}

/// <summary>The rules of sessions on this PC, in HKLM\SOFTWARE\POLARIS\Agent\Relacie (only administrators and the agent
/// change them; a recipe cannot touch this part of the registry directly).</summary>
[SupportedOSPlatform("windows")]
public static class SessionRules
{
    public const string Key = @"SOFTWARE\POLARIS\Agent\Relacie";

    public static Dictionary<string, SessionCleaning> Read()
    {
        var rules = new Dictionary<string, SessionCleaning>(StringComparer.OrdinalIgnoreCase);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(Key);
        foreach (var name in key?.GetValueNames() ?? [])
        {
            if (Enum.TryParse<SessionCleaning>(key!.GetValue(name) as string, out var mode))
            {
                rules[name] = mode;
            }
        }

        return rules;
    }

    public static void Set(string account, SessionCleaning? mode)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(Key, writable: true);
        if (mode is { } m)
        {
            key.SetValue(account, m.ToString(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(account, throwOnMissingValue: false);
        }
    }
}

/// <summary>The session element: writes the rule for the cleaner. A missing account or an account of an administrator is
/// refused – the profile of an administrator is never deleted.</summary>
[SupportedOSPlatform("windows")]
public sealed class SessionRuleHandler : IElementHandler
{
    public bool CanHandle(Element element) => element is SessionElement;

    public Observation Observe(Element element)
    {
        var s = (SessionElement)element;
        var account = s.Account.Trim();
        if (!SessionRules.Read().TryGetValue(account, out var mode))
        {
            return new Observation(Observed.Absent, "pravidlo nie je nastavené");
        }

        return mode == s.Clean ? new Observation(Observed.Present, Describe(mode)) : new Observation(Observed.Different, $"je {Describe(mode)}, má byť {Describe(s.Clean)}");
    }

    public void Apply(Element element)
    {
        var s = (SessionElement)element;
        var account = s.Account.Trim();
        if (s.Present)
        {
            if (Profiles.SidOf(account) is null)
            {
                throw new InvalidOperationException($"Konto {account} na tomto PC neexistuje – najprv ho vytvor (bez práv správcu).");
            }

            if (Profiles.IsAdministrator(account))
            {
                throw new InvalidOperationException($"Konto {account} je správca – jeho profil POLARIS nikdy nemaže.");
            }
        }

        SessionRules.Set(account, s.Present ? s.Clean : null);
    }

    public static string Describe(SessionCleaning mode) => mode switch
    {
        SessionCleaning.AtSignOut => "čistiť po odhlásení",
        SessionCleaning.EndOfDay => "čistiť po skončení dňa",
        SessionCleaning.Restore => "po odhlásení vrátiť z trezoru",
        SessionCleaning.EndOfWeek => "čistiť po skončení týždňa",
        _ => "nečistiť",
    };
}

/// <summary>Deletes the profile of a shared account when its rule says so – never while somebody is signed in to it and
/// never the profile of an administrator. The next sign-in makes a fresh profile from the template.</summary>
[SupportedOSPlatform("windows")]
public static class SessionCleaner
{
    public const string AlreadyClean = "profil je už čistý";
    public const string Preparing = "pripravuje sa trezor – profil sa nečistí, kým ho neuložíš (polaris-agent trezor ulozit)";

    /// <summary>The profile of the account waits for cleaning or for being put back (the automatic sign-in waits then).</summary>
    public static bool Pending(string account)
    {
        if (!SessionRules.Read().TryGetValue(account, out var mode) || mode is not (SessionCleaning.AtSignOut or SessionCleaning.Restore)
            || Profiles.SidOf(account) is not { } sid)
        {
            return false;
        }

        var state = Profiles.State(sid);
        if (!state.Exists || state.Loaded)
        {
            return false;
        }

        if (mode == SessionCleaning.AtSignOut)
        {
            return true;
        }

        var vault = new ProfileVault();
        return !vault.Preparing(account) && (vault.Unfit(account, sid) is not null || !vault.UpToDate(account, sid));
    }

    public sealed record Result(bool Cleaned, string Message);

    /// <param name="beforeDelete">Runs just before the profile is deleted (the programs of the School store of the session
    /// are removed there); its failure is reported, the profile is deleted anyway.</param>
    public static Result Clean(string account, SessionCleaning mode, DateTime now, bool force = false, Func<string, string?>? beforeDelete = null, ProfileVault? vault = null)
    {
        if (SessionPolicy.NeverClean.Contains(account))
        {
            return new Result(false, $"konto {account} sa nikdy nečistí");
        }

        if (Profiles.SidOf(account) is not { } sid)
        {
            return new Result(false, $"konto {account} neexistuje");
        }

        if (Profiles.IsAdministrator(account))
        {
            return new Result(false, $"konto {account} je správca – nečistí sa");
        }

        var state = Profiles.State(sid);
        if (!state.Exists)
        {
            return new Result(false, AlreadyClean);
        }

        if (state.Loaded)
        {
            return new Result(false, "na konte je niekto prihlásený – vyčistí sa po odhlásení");
        }

        if (!force && !SessionPolicy.ShouldClean(mode, state.Loaded, state.Exists, state.LastUsed, now))
        {
            return new Result(false, "podľa pravidla ešte nie je čas");
        }

        string? unfit = null;
        if (mode == SessionCleaning.Restore && !force)
        {
            vault ??= new ProfileVault();
            if (vault.Preparing(account))
            {
                return new Result(false, Preparing);
            }

            unfit = vault.Unfit(account, sid);
            if (unfit is null)
            {
                if (vault.UpToDate(account, sid))
                {
                    return new Result(false, AlreadyClean);
                }

                var before = beforeDelete?.Invoke(account);
                var restored = vault.Restore(account, sid);
                return new Result(true, $"profil konta {account} vrátený z trezoru (zmazané {restored.Removed}, vrátené {restored.Put}, "
                    + $"{Polaris.Core.Format.Seconds(restored.Seconds)}){(restored.Note is { } n ? "; " + n : "")}{(before is { Length: > 0 } ? "; " + before : "")}");
            }
        }

        var note = beforeDelete?.Invoke(account);
        Profiles.Delete(sid);
        return new Result(true, $"profil konta {account} zmazaný, ďalšie prihlásenie začne načisto{(unfit is null ? "" : $" (trezor: {unfit})")}{(note is { Length: > 0 } ? "; " + note : "")}");
    }
}
