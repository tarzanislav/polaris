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
        Net("user", account, "Ab1-" + Guid.NewGuid().ToString("N")[..10], "/add");
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

    [LibraryImport("userenv.dll", EntryPoint = "CreateProfile", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateProfile(string sid, string userName, [Out] char[] profilePath, uint length);
}
