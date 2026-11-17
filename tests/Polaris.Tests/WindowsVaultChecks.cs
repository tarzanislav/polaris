using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Polaris.Agent;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>The trezor of profiles on a real Windows (the CI runner as an administrator): a real account with a real
/// profile is saved, changed as a pupil would change it, put back – files, folders, junctions, attributes and times –
/// and the account is never left locked. Elsewhere it passes without doing anything.</summary>
public static class WindowsVaultChecks
{
    [Check]
    public static void AProfileIsSavedAndPutBackExactly()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        Run();
    }

    [SupportedOSPlatform("windows")]
    private static void Run()
    {
        var account = "poltrezor" + Guid.NewGuid().ToString("N")[..4];
        var folder = Path.Combine(Path.GetTempPath(), "polaris-trezor-" + Guid.NewGuid().ToString("N")[..8]);
        var stateKey = @"SOFTWARE\PolarisSkuska\Trezor" + Guid.NewGuid().ToString("N")[..8];
        var vault = new ProfileVault(folder, stateKey);
        Net("user", account, "Ab1-" + Guid.NewGuid().ToString("N")[..10], "/add");
        try
        {
            var sid = Profiles.SidOf(account) ?? throw new CheckFailed("konto sa nevytvorilo");
            var path = new System.Text.StringBuilder(260);
            Expect.Equal(0, CreateProfile(sid, account, path, (uint)path.Capacity), "vytvorenie profilu");
            var profile = path.ToString();

            // the prepared profile: a document, a read-only file, a cache that is never kept
            foreach (var known in new[] { "Desktop", "Documents", "Downloads" })
            {
                Directory.CreateDirectory(Path.Combine(profile, known));
            }

            File.WriteAllText(Path.Combine(profile, "Desktop", "pripravene.txt"), "vyladené správcom");
            var readOnly = Path.Combine(profile, "Documents", "len-na-citanie.txt");
            File.WriteAllText(readOnly, "nemeniť");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly | FileAttributes.Hidden);
            Directory.CreateDirectory(Path.Combine(profile, @"AppData\Local\Temp"));
            File.WriteAllText(Path.Combine(profile, @"AppData\Local\Temp\docasne.tmp"), "x");

            Expect.True(vault.Unfit(account, sid) is not null, "bez trezoru nesedí");
            var saved = vault.Save(account, () => DateTimeOffset.UtcNow);
            Expect.True(saved.Entries > 5 && saved.Bytes > 0, $"uložené: {saved.Entries} položiek, {saved.Bytes} B");
            Expect.True(vault.Unfit(account, sid) is null, "trezor sedí k profilu");
            var manifest = vault.Manifest(account)!;
            Expect.True(manifest.Entries.Any(e => e.Path == @"AppData\Local\Temp") && !manifest.Entries.Any(e => e.Path.StartsWith(@"AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase)),
                "dočasné súbory sa neukladajú, len ich priečinok");
            var links = manifest.Entries.Where(e => e.Kind == VaultKind.Link).ToList();
            Expect.True(links.Count > 0, "staré prepojenia profilu (Application Data …) sú v trezore ako prepojenia");
            Expect.True(vault.UpToDate(account, sid), "hneď po uložení nie je čo vracať");
            var before = ProfileArchive.Read(profile, skipCaches: true);

            // the pupil's session: new files, a changed one, a deleted one, a removed junction, a new folder
            File.WriteAllText(Path.Combine(profile, "Desktop", "ziak.txt"), "moja práca");
            File.SetAttributes(readOnly, FileAttributes.Normal);
            File.WriteAllText(readOnly, "zmenené");
            File.Delete(Path.Combine(profile, "Desktop", "pripravene.txt"));
            Directory.CreateDirectory(Path.Combine(profile, "Downloads", "hra"));
            File.WriteAllText(Path.Combine(profile, "Downloads", "hra", "setup.exe"), "nie");
            var link = Path.Combine(profile, links[0].Path);
            ProfileArchive.Remove(link, folder: false);
            Expect.True(!Directory.Exists(link), "prepojenie zmizlo");
            ForceRestore(stateKey, account);

            var result = SessionCleaner.Clean(account, SessionCleaning.Restore, DateTime.Now, vault: vault);
            Expect.True(result.Cleaned && result.Message.Contains("trezoru", StringComparison.Ordinal), "vrátené: " + result.Message);
            Expect.True(!File.Exists(Path.Combine(profile, "Desktop", "ziak.txt")) && !Directory.Exists(Path.Combine(profile, "Downloads", "hra")), "práca žiaka zmizla");
            Expect.Equal("vyladené správcom", File.ReadAllText(Path.Combine(profile, "Desktop", "pripravene.txt")), "zmazaný súbor je späť");
            Expect.Equal("nemeniť", File.ReadAllText(readOnly), "zmenený súbor je späť");
            Expect.Equal(FileAttributes.ReadOnly | FileAttributes.Hidden, File.GetAttributes(readOnly) & (FileAttributes.ReadOnly | FileAttributes.Hidden), "aj jeho vlastnosti");
            Expect.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "prepojenie je späť ako prepojenie");
            Expect.True(!File.Exists(Path.Combine(profile, @"AppData\Local\Temp\docasne.tmp")), "dočasné súbory zmizli");
            Expect.True(VaultRules.Plan(ProfileArchive.Read(profile, skipCaches: false), manifest).Nothing, "profil je presne ako trezor");
            Expect.Equal(before.Count, ProfileArchive.Read(profile, skipCaches: true).Count, "rovnaký počet položiek");
            Expect.True((Accounts.Flags(account) & Accounts.Disabled) == 0, "konto nie je zablokované");
            Expect.True(SessionCleaner.Clean(account, SessionCleaning.Restore, DateTime.Now, vault: vault).Message == SessionCleaner.AlreadyClean, "druhýkrát niet čo vracať");

            // a profile made again does not fit the old trezor – deleted as without one
            Profiles.Delete(sid);
            Expect.Equal(0, CreateProfile(sid, account, path, (uint)path.Capacity), "nový profil");
            Expect.True(vault.Unfit(account, sid) is { } why && why.Contains("znova", StringComparison.Ordinal), "nový profil k trezoru nesedí");
            var deleted = SessionCleaner.Clean(account, SessionCleaning.Restore, DateTime.Now, vault: vault);
            Expect.True(deleted.Cleaned && deleted.Message.Contains("zmazaný", StringComparison.Ordinal), "zmazaný ako bez trezoru: " + deleted.Message);
        }
        finally
        {
            vault.UnlockLeftovers();
            vault.Forget(account);
            Registry.LocalMachine.DeleteSubKeyTree(stateKey, throwOnMissingSubKey: false);
            if (Profiles.SidOf(account) is { } sid && Profiles.State(sid).Exists)
            {
                Profiles.Delete(sid);
            }

            Net("user", account, "/delete");
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>On the runner nobody signs in, so the time of the last sign-in does not change: the mark "already put back"
    /// is taken away as a sign-in would.</summary>
    [SupportedOSPlatform("windows")]
    private static void ForceRestore(string stateKey, string account)
    {
        using var key = Registry.LocalMachine.OpenSubKey(stateKey + "\\" + account, writable: true);
        key?.DeleteValue("Obnovene", throwOnMissingValue: false);
    }

    [System.Runtime.InteropServices.DllImport("userenv.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CreateProfile(string sid, string userName, System.Text.StringBuilder profilePath, uint length);

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

    private static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
