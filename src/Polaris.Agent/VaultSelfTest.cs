using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>A check of the trezor on this very PC (polaris-agent skuska-trezoru, run as the system – the service's
/// account – because saving a profile needs the backup and restore privileges): a throw-away account with a real
/// profile is saved, changed as a pupil would change it and put back – files, folders, junctions, attributes – and the
/// account is never left locked. Everything is removed at the end.</summary>
[SupportedOSPlatform("windows")]
public static partial class VaultSelfTest
{
    public static List<(bool Ok, string What)> Run()
    {
        var results = new List<(bool, string)>();
        void Expect(bool ok, string what) => results.Add((ok, what));

        var account = "poltrezor" + Guid.NewGuid().ToString("N")[..4];
        var folder = Path.Combine(Path.GetTempPath(), "polaris-trezor-" + Guid.NewGuid().ToString("N")[..8]);
        var stateKey = @"SOFTWARE\PolarisSkuska\Trezor" + Guid.NewGuid().ToString("N")[..8];
        var vault = new ProfileVault(folder, stateKey);
        var password = "Ab1-" + Guid.NewGuid().ToString("N")[..10];
        Net("user", account, password, "/add");
        try
        {
            var sid = Profiles.SidOf(account) ?? throw new InvalidOperationException("skúšobné konto sa nevytvorilo");
            var path = new char[260];
            Expect(CreateProfile(sid, account, path, (uint)path.Length) == 0, "skúšobný profil vytvorený");
            var profile = new string(path).TrimEnd('\0');

            // the prepared profile: a document, a read-only hidden file, a cache that is never kept
            foreach (var known in new[] { "Desktop", "Documents", "Downloads", @"AppData\Local\Temp" })
            {
                Directory.CreateDirectory(Path.Combine(profile, known));
            }

            File.WriteAllText(Path.Combine(profile, "Desktop", "pripravene.txt"), "vyladené správcom");
            var readOnly = Path.Combine(profile, "Documents", "len-na-citanie.txt");
            File.WriteAllText(readOnly, "nemeniť");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly | FileAttributes.Hidden);
            File.WriteAllText(Path.Combine(profile, @"AppData\Local\Temp\docasne.tmp"), "x");
            var pins = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
            Directory.CreateDirectory(Path.Combine(profile, StartLayoutRules.Folder));
            File.WriteAllBytes(Path.Combine(profile, StartLayoutRules.Folder, "start2.bin"), pins);

            Expect(vault.Unfit(account, sid) is not null, "bez trezoru nesedí");
            var saved = vault.Save(account, () => DateTimeOffset.UtcNow);
            Expect(saved.Entries > 5 && saved.Bytes > 0, $"uložené: {saved.Entries} položiek, {Measurement.Size(saved.Bytes)}, {Polaris.Core.Format.Seconds(saved.Seconds)}");
            Expect(vault.Unfit(account, sid) is null, "trezor sedí k profilu");
            var manifest = vault.Manifest(account)!;
            Expect(manifest.Entries.Any(e => e.Path.Equals(@"AppData\Local\Temp", StringComparison.OrdinalIgnoreCase))
                && !manifest.Entries.Any(e => e.Path.StartsWith(@"AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase)), "dočasné súbory sa neukladajú, len ich priečinok");
            var links = manifest.Entries.Where(e => e.Kind == VaultKind.Link).ToList();
            Expect(links.Count > 0, $"staré prepojenia profilu sú v trezore ako prepojenia ({links.Count})");
            Expect(vault.UpToDate(account, sid), "hneď po uložení nie je čo vracať");

            // the pins of Start from the trezor into a template of profiles (a folder of the check, never the real one)
            var template = Path.Combine(folder, "predloha");
            var templatePins = Path.Combine(template, StartLayoutRules.Folder, "start2.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(templatePins)!);
            File.WriteAllBytes(templatePins, [1, 2, 3]); // what Windows had there
            var start = new StartLayoutHandler(vault, template, stateKey + "-start");
            var layout = new StartLayoutElement { Id = "start", From = account };
            Expect(start.Observe(layout).State == Observed.Absent, "pripnuté v Štarte: na začiatku pravidlo nie je");
            start.Apply(layout);
            Expect(start.Observe(layout).State == Observed.Present && File.ReadAllBytes(templatePins).AsSpan().SequenceEqual(pins), "pripnuté v Štarte z trezoru sú v predlohe profilov, bajt po bajte");
            Expect(File.Exists(templatePins + StartLayoutHandler.Original), "pôvodný súbor predlohy odložený");
            Expect(start.AfterSave(account)?.Contains("nový profil", StringComparison.Ordinal) == true, "po uložení trezoru sa rozloženie prevezme znova");
            layout.Present = false;
            start.Apply(layout);
            Expect(start.Observe(layout).State == Observed.Absent && File.ReadAllBytes(templatePins).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "bez prvku má predloha znova pôvodný súbor");
            var before = ProfileArchive.Read(profile, skipCaches: true).Count;

            // the pupil's session: a new file, a changed one, a deleted one, a removed junction, a new folder
            File.WriteAllText(Path.Combine(profile, "Desktop", "ziak.txt"), "moja práca");
            File.SetAttributes(readOnly, FileAttributes.Normal);
            File.WriteAllText(readOnly, "zmenené");
            File.Delete(Path.Combine(profile, "Desktop", "pripravene.txt"));
            Directory.CreateDirectory(Path.Combine(profile, "Downloads", "hra"));
            File.WriteAllText(Path.Combine(profile, "Downloads", "hra", "setup.exe"), "nie");
            var link = links.Count > 0 ? Path.Combine(profile, links[0].Path) : null;
            if (link is not null)
            {
                ProfileArchive.Remove(link, folder: false);
            }

            using (var key = Registry.LocalMachine.OpenSubKey(stateKey + "\\" + account, writable: true))
            {
                key?.DeleteValue("Obnovene", throwOnMissingValue: false); // as a sign-in would
            }

            var result = SessionCleaner.Clean(account, SessionCleaning.Restore, DateTime.Now, vault: vault);
            Expect(result.Cleaned && result.Message.Contains("trezoru", StringComparison.Ordinal), result.Message);
            Expect(!File.Exists(Path.Combine(profile, "Desktop", "ziak.txt")) && !Directory.Exists(Path.Combine(profile, "Downloads", "hra")), "práca žiaka zmizla");
            Expect(File.Exists(Path.Combine(profile, "Desktop", "pripravene.txt")) && File.ReadAllText(Path.Combine(profile, "Desktop", "pripravene.txt")) == "vyladené správcom", "zmazaný súbor je späť");
            Expect(File.ReadAllText(readOnly) == "nemeniť", "zmenený súbor je späť");
            Expect((File.GetAttributes(readOnly) & (FileAttributes.ReadOnly | FileAttributes.Hidden)) == (FileAttributes.ReadOnly | FileAttributes.Hidden), "aj jeho vlastnosti (len na čítanie, skrytý)");
            Expect(link is null || (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "prepojenie je späť ako prepojenie");
            Expect(!File.Exists(Path.Combine(profile, @"AppData\Local\Temp\docasne.tmp")), "dočasné súbory zmizli");
            Expect(VaultRules.Plan(ProfileArchive.Read(profile, skipCaches: false), manifest).Nothing, "profil je presne ako trezor");
            Expect(before == ProfileArchive.Read(profile, skipCaches: true).Count, "rovnaký počet položiek");
            Expect((Accounts.Flags(account) & Accounts.Disabled) == 0, "konto nie je zablokované");
            Expect(SessionCleaner.Clean(account, SessionCleaning.Restore, DateTime.Now, vault: vault).Message == SessionCleaner.AlreadyClean, "druhýkrát niet čo vracať");

            // a real sign-in and sign-out (the registry of the profile as Windows leaves it, its changes partly still in
            // the logs), then a value of a recipe into it – as after the sign-out of a pupil
            var signIn = SignInAndOut(account, password);
            Expect(string.IsNullOrEmpty(signIn), "prihlásenie a odhlásenie skúšobného konta" + (string.IsNullOrEmpty(signIn) ? "" : ": " + signIn));
            var value = new UserRegistryElement { Id = "hodnota", Path = @"Software\PolarisSkuska", Name = "PoOdhlaseni", Kind = RegistryKind.DWord, Data = "7" };
            try
            {
                using (var opened = ProfileValues.OpenProfile(Path.Combine(profile, "NTUSER.DAT"), "profilu"))
                {
                    Expect(true, opened.AsWindows
                        ? "register po odhlásení otvorený ako pri prihlásení (súkromne ho Windows neotvoril)"
                        : "register po odhlásení otvorený súkromne");
                }

                ProfileValues.InProfile(sid, r =>
                {
                    ProfileValues.Write(r, value);
                    r.Flush();
                    return true;
                });
                Expect(ProfileValues.InProfile(sid, r => ProfileValues.Read(r, value))?.State == Observed.Present, "hodnota receptu v registri profilu po skutočnom odhlásení");
            }
            catch (Exception e) when (e is SystemCallException or InvalidOperationException)
            {
                Expect(false, "hodnota receptu do profilu po odhlásení: " + e.Message);
            }

            // the way of Windows itself (what the pilot needed: privately Windows answered 1009 there)
            using (var windows = ProfileValues.OpenProfile(Path.Combine(profile, "NTUSER.DAT"), "profilu", asWindows: true))
            {
                value.Data = "9";
                ProfileValues.Write(windows.Root, value);
                windows.Root.Flush();
                Expect(windows.AsWindows && ProfileValues.Read(windows.Root, value).State == Observed.Present, "register profilu načítaný ako pri prihlásení: hodnota zapísaná");
            }

            Expect(ProfileValues.InProfile(sid, r => ProfileValues.Read(r, value))?.State == Observed.Present, "po uvoľnení registra hodnota ostala v súbore profilu");
            value.Data = "8";
            Expect(ProfileValues.AfterRestore(profile, [value]) is null && ProfileValues.InProfile(sid, r => ProfileValues.Read(r, value))?.State == Observed.Present, "hodnoty receptu po návrate z trezoru do takého registra");

            // a profile made again does not fit the old trezor – deleted as without one
            Profiles.Delete(sid);
            Expect(CreateProfile(sid, account, path, (uint)path.Length) == 0, "nový profil");
            Expect(vault.Unfit(account, sid) is { } why && why.Contains("znova", StringComparison.Ordinal), "nový profil k starému trezoru nesedí");
            var deleted = SessionCleaner.Clean(account, SessionCleaning.Restore, DateTime.Now, vault: vault);
            Expect(deleted.Cleaned && deleted.Message.Contains("zmazaný", StringComparison.Ordinal), "zmazaný ako bez trezoru");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Expect(false, "chyba: " + e.Message);
        }
        finally
        {
            try
            {
                vault.UnlockLeftovers();
                vault.Forget(account);
                Registry.LocalMachine.DeleteSubKeyTree(stateKey, throwOnMissingSubKey: false);
                Registry.LocalMachine.DeleteSubKeyTree(stateKey + "-start", throwOnMissingSubKey: false);
                if (Profiles.SidOf(account) is { } sid && Profiles.State(sid).Exists)
                {
                    Profiles.Delete(sid);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Expect(false, "upratanie: " + e.Message);
            }

            Net("user", account, "/delete");
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        return results;
    }

    private static void Net(params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "net.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var p = System.Diagnostics.Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    /// <summary>Signs the account in and out as Windows does (its profile and registry loaded, a value changed, unloaded).
    /// Null or empty when it went; else why not.</summary>
    private static string? SignInAndOut(string account, string password)
    {
        Polaris.Windows.Privileges.Enable(Polaris.Windows.Privileges.Backup);
        Polaris.Windows.Privileges.Enable(Polaris.Windows.Privileges.Restore);
        if (!LogonUser(account, ".", password, 2 /* interactive */, 0, out var token))
        {
            return "LogonUser: chyba " + Marshal.GetLastPInvokeError();
        }

        try
        {
            var info = new ProfileInfo { Size = Marshal.SizeOf<ProfileInfo>(), Flags = 1 /* PI_NOUI */, UserName = Marshal.StringToHGlobalUni(account) };
            try
            {
                if (!LoadUserProfile(token, ref info))
                {
                    return "LoadUserProfile: chyba " + Marshal.GetLastPInvokeError();
                }

                using (var user = Registry.Users.OpenSubKey(Profiles.SidOf(account)!, writable: true))
                using (var key = user?.CreateSubKey(@"Software\PolarisSkuska", writable: true))
                {
                    key?.SetValue("Prihlaseny", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                }

                return UnloadUserProfile(token, info.Profile) ? "" : "UnloadUserProfile: chyba " + Marshal.GetLastPInvokeError();
            }
            finally
            {
                Marshal.FreeHGlobal(info.UserName);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProfileInfo
    {
        public int Size;
        public int Flags;
        public nint UserName;
        public nint ProfilePath;
        public nint DefaultPath;
        public nint ServerName;
        public nint PolicyPath;
        public nint Profile;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUser(string user, string domain, string password, int type, int provider, out nint token);

    [LibraryImport("userenv.dll", EntryPoint = "LoadUserProfileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LoadUserProfile(nint token, ref ProfileInfo info);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnloadUserProfile(nint token, nint profile);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("userenv.dll", EntryPoint = "CreateProfile", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateProfile(string sid, string userName, [Out] char[] profilePath, uint length);
}
