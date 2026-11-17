using System.Security.Principal;
using Microsoft.Win32;
using Polaris.Agent;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>The real executors on a real registry. They run only on Windows with the rights of an administrator (the CI
/// runner has them); elsewhere they pass without doing anything. Everything is written under own test keys and removed.</summary>
public static class WindowsAgentChecks
{
    private const string TestRoot = @"SOFTWARE\PolarisSkuska";

    [Check]
    public static void RegistryValuesAreWrittenCheckedAndRemoved()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var key = $@"{TestRoot}\{Guid.NewGuid():N}";
        var handler = new RegistryHandler();
        RegistryValueElement[] values =
        [
            new() { Id = "d", Path = @"HKLM\" + key, Name = "Cislo", Kind = RegistryKind.DWord, Data = "0xFFFFFFFF" },
            new() { Id = "q", Path = @"HKLM\" + key, Name = "Velke", Kind = RegistryKind.QWord, Data = "18446744073709551615" },
            new() { Id = "s", Path = @"HKLM\" + key, Name = "Text", Kind = RegistryKind.String, Data = "Škola – záložky" },
            new() { Id = "e", Path = @"HKLM\" + key, Name = "Cesta", Kind = RegistryKind.ExpandString, Data = @"%ProgramData%\POLARIS" },
            new() { Id = "m", Path = @"HKLM\" + key, Name = "Riadky", Kind = RegistryKind.MultiString, Data = "a\nb" },
            new() { Id = "p", Path = @"HKLM\" + key, Name = "", Kind = RegistryKind.String, Data = "predvolená" },
            new() { Id = "b", Path = @"HKLM\" + key, Name = "Bajty", Kind = RegistryKind.Binary, Data = "90 12 03 80 10 00 00 00" },
        ];
        try
        {
            foreach (var v in values)
            {
                Expect.Equal(Observed.Absent, handler.Observe(v).State, $"{v.Id}: na začiatku nie je");
                handler.Apply(v);
                Expect.Equal(Observed.Present, handler.Observe(v).State, $"{v.Id}: po zápise je presne taká");
            }

            var changed = new RegistryValueElement { Id = "d2", Path = values[0].Path, Name = "Cislo", Kind = RegistryKind.DWord, Data = "1" };
            var seen = handler.Observe(changed);
            Expect.True(seen.State == Observed.Different && seen.Detail.Contains("4294967295", StringComparison.Ordinal), "iná hodnota: " + seen.Detail);
            var wrongKind = new RegistryValueElement { Id = "s2", Path = values[2].Path, Name = "Text", Kind = RegistryKind.ExpandString, Data = values[2].Data };
            Expect.Equal(Observed.Different, handler.Observe(wrongKind).State, "iný druh hodnoty");

            foreach (var v in values)
            {
                v.Present = false;
                Expect.Equal(Observed.Present, handler.Observe(v).State, $"{v.Id}: je tam na odstránenie");
                handler.Apply(v);
                Expect.Equal(Observed.Absent, handler.Observe(v).State, $"{v.Id}: po odstránení nie je");
            }
        }
        finally
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            root.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        }
    }

    [Check]
    public static void InstalledProgramsAreRecognisedByName()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        var entry = $@"{InstalledPrograms.UninstallKey}\PolarisSkuska-{id}";
        var element = new ProgramElement { Id = "skuska", DisplayName = $"POLARIS skúšobný program {id}", Winget = "Polaris.Skuska" };
        var handler = new ProgramHandler(Path.GetTempPath());
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        try
        {
            Expect.Equal(Observed.Absent, handler.Observe(element).State, "pred zápisom nie je");
            using (var key = root.CreateSubKey(entry, writable: true))
            {
                key.SetValue("DisplayName", element.DisplayName);
                key.SetValue("DisplayVersion", "1.2.3");
                key.SetValue("QuietUninstallString", "\"C:\\Program Files\\Skuska\\uninst.exe\" /S");
            }

            var seen = handler.Observe(element);
            Expect.True(seen.State == Observed.Present && seen.Detail.Contains("1.2.3", StringComparison.Ordinal), "rozpoznaný s verziou: " + seen.Detail);
            var listed = InstalledPrograms.Read().Single(p => p.DisplayName == element.DisplayName);
            Expect.Equal("\"C:\\Program Files\\Skuska\\uninst.exe\" /S", listed.QuietUninstallString, "tichá odinštalácia");
            Expect.True(!listed.WindowsInstaller, "nie je Windows Installer");
        }
        finally
        {
            root.DeleteSubKeyTree(entry, throwOnMissingSubKey: false);
        }

        Expect.Equal(Observed.Absent, handler.Observe(element).State, "po zmazaní nie je");
    }

    [Check]
    public static void AMissingServiceIsAsOffAsCanBe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handler = new ServiceHandler();
        Expect.Equal(Observed.Present, handler.Observe(new ServiceElement { Id = "s", Name = "PolarisNeexistuje", Start = ServiceStart.Disabled }).State, "vypnutá služba, ktorá na PC nie je, je v poriadku");
        Expect.Throws<ServiceMissingException>(
            () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    handler.Observe(new ServiceElement { Id = "s", Name = "PolarisNeexistuje", Start = ServiceStart.Manual });
                }
            },
            "zapnúť sa neexistujúca služba nedá");
    }

    [Check]
    public static void ServiceStartIsSetAndChecked()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        // an own service that never runs (its program does not exist) – only its settings are changed
        var name = "PolarisSkuska" + Guid.NewGuid().ToString("N")[..8];
        Sc("create", name, "binPath=", @"C:\neexistuje\polaris-skuska.exe", "start=", "demand");
        try
        {
            var handler = new ServiceHandler();
            var manual = new ServiceElement { Id = "s", Name = name, Start = ServiceStart.Manual, Running = false };
            Expect.Equal(Observed.Present, handler.Observe(manual).State, "na začiatku ručne a nebeží");
            foreach (var start in new[] { ServiceStart.Disabled, ServiceStart.AutomaticDelayed, ServiceStart.Automatic, ServiceStart.Manual })
            {
                var wanted = new ServiceElement { Id = "s", Name = name, Start = start };
                Expect.Equal(Observed.Different, handler.Observe(wanted).State, $"{start}: pred zmenou iné (každý krok mení spúšťanie)");
                handler.Apply(wanted);
                var seen = handler.Observe(wanted);
                Expect.Equal(Observed.Present, seen.State, $"{start}: po zmene ({seen.Detail})");
            }

            var missing = new Engine([handler]).Plan([new ServiceElement { Id = "x", Name = name + "x" }]);
            Expect.True(missing.Items.Single().Kind == ChangeKind.Cannot && missing.Items.Single().Before.Contains("neexistuje", StringComparison.Ordinal), "neexistujúca služba");
        }
        finally
        {
            Sc("delete", name);
        }
    }

    [Check]
    public static void ServicesByPrefixPhysicalOnlyAndPutBack()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var prefix = "PolarisPredpona" + Guid.NewGuid().ToString("N")[..6];
        var names = new[] { prefix + "A", prefix + "B" };
        var memoryKey = @"SOFTWARE\POLARIS\Skuska\Sluzby" + Guid.NewGuid().ToString("N")[..6];
        foreach (var n in names)
        {
            Sc("create", n, "binPath=", @"C:\neexistuje\polaris-skuska.exe", "start=", "demand");
        }

        try
        {
            var memory = new ServiceMemory(memoryKey);
            var handler = new ServiceHandler(() => false, memory);
            var all = new ServiceElement { Id = "p", Name = prefix + "*", Start = ServiceStart.Disabled };
            Expect.Equal(Observed.Different, handler.Observe(all).State, "obe služby zatiaľ ručne");
            handler.Apply(all);
            var seen = handler.Observe(all);
            Expect.Equal(Observed.Present, seen.State, "obe vypnuté: " + seen.Detail);
            Expect.Equal(2, memory.All().Count, "pôvodný stav oboch zapamätaný");
            Expect.Equal("Manual", memory.All()[names[0]], "pôvodne ručne");

            handler.Apply(new ServiceElement { Id = "q", Name = names[0], Start = ServiceStart.Automatic });
            Expect.Equal("Manual", memory.All()[names[0]], "ďalšia zmena prvý stav neprepíše");

            Expect.True(memory.Restore(names[0]) && memory.Restore(names[1]), "vrátené");
            Expect.Equal(Observed.Present, handler.Observe(new ServiceElement { Id = "r", Name = prefix + "*", Start = ServiceStart.Manual }).State, "obe znova ručne");
            Expect.Equal(0, memory.All().Count, "po vrátení zabudnuté");

            var onVirtual = new ServiceHandler(() => true, memory);
            var physical = new ServiceElement { Id = "f", Name = names[0], Start = ServiceStart.Disabled, PhysicalOnly = true };
            Expect.Equal(Observed.Present, onVirtual.Observe(physical).State, "na virtuálnom PC sa prvok pre fyzické PC vynechá");
            onVirtual.Apply(physical);
            Expect.Equal(Observed.Present, handler.Observe(new ServiceElement { Id = "g", Name = names[0], Start = ServiceStart.Manual }).State, "a nič nezmení");
            Expect.Equal(Observed.Present, handler.Observe(new ServiceElement { Id = "h", Name = "PolarisNicTakeho*", Start = ServiceStart.Disabled }).State, "žiadna služba so začiatkom = v poriadku");
            Console.WriteLine($"  (tento PC je {(Machine.IsVirtual() ? "virtuálny" : "fyzický")})");
        }
        finally
        {
            foreach (var n in names)
            {
                Sc("delete", n);
            }

            Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(memoryKey, throwOnMissingSubKey: false);
        }
    }

    [Check]
    public static void FeatureStateIsReadThroughDism()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var handler = new FeatureHandler();
        var smb1 = handler.Observe(new FeatureElement { Id = "f", Name = "SMB1Protocol" });
        Expect.True(smb1.State is Observed.Absent or Observed.Present, "stav SMB1: " + smb1.Detail);
        var plan = new Engine([handler]).Plan([new FeatureElement { Id = "x", Name = "PolarisNeexistujucaSucast" }]);
        Expect.True(plan.Items.Single().Kind == ChangeKind.Cannot && plan.Items.Single().Before.Contains("neexistuje", StringComparison.Ordinal), "neznáma súčasť: " + plan.Items.Single().Before);
    }

    [Check]
    public static void TrustedConsolesLiveInTheProtectedRegistry()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var (_, pub) = Polaris.Net.RecipeSigning.NewConsoleKey("SkuskaCI", "heslo pre skusku CI");
        try
        {
            TrustStore.Add(pub);
            Expect.True(TrustStore.Read().Any(t => t.Fingerprint == pub.Fingerprint && t.Name == "SkuskaCI"), "konzola v zozname");
            Expect.True(TrustStore.Remove(pub.Fingerprint), "odstránenie");
            Expect.True(TrustStore.Read().All(t => t.Fingerprint != pub.Fingerprint), "po odstránení nie je");
        }
        finally
        {
            TrustStore.Remove(pub.Fingerprint);
        }
    }

    [Check]
    public static void TemplateOfProfilesGetsValues()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        // a copy of the template of profiles, never the real one
        var copy = Path.Combine(Path.GetTempPath(), $"polaris-predloha-{Guid.NewGuid():N}.dat");
        File.Copy(ProfileTemplateHandler.DefaultHiveFile(), copy);
        try
        {
            var value = new UserRegistryElement { Id = "u", Path = @"Software\PolarisSkuska", Name = "Hodnota", Kind = RegistryKind.DWord, Data = "7" };
            using (var handler = new ProfileTemplateHandler(copy))
            {
                Expect.Equal(Observed.Absent, handler.Observe(value).State, "na začiatku nie je");
                handler.Apply(value);
                Expect.Equal(Observed.Present, handler.Observe(value).State, "po zápise je");
            }

            using (var again = new ProfileTemplateHandler(copy))
            {
                Expect.Equal(Observed.Present, again.Observe(value).State, "zápis ostal v súbore");
                var keyboards = new KeyboardsElement { Id = "k", Languages = ["sk-SK", "en-US"] };
                var engine = new Engine([again]);
                var run = engine.Apply(engine.Plan([keyboards]), null, () => DateTimeOffset.UtcNow);
                Expect.True(run.Failed == 0, "klávesnice do predlohy: " + RunReport.RunText(run));
                Expect.True(engine.Plan([keyboards]).InDesiredState, "klávesnice sedia");

                var quiet = new SoundsElement { Id = "z" };
                var before = again.Observe(quiet);
                Console.WriteLine("  (zvuky v predlohe pred zmenou: " + before.Detail + ")");
                again.Apply(quiet);
                var after = again.Observe(quiet);
                Expect.Equal(Observed.Present, after.State, "predloha bez zvukov: " + after.Detail);
            }
        }
        finally
        {
            File.Delete(copy);
            foreach (var log in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(copy) + "*"))
            {
                File.Delete(log);
            }
        }
    }

    [Check]
    public static void SharedAccountProfileIsCleanedButNeverAnAdministrator()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var account = "polskuska" + Guid.NewGuid().ToString("N")[..4];
        Run("net.exe", "user", account, "Ab1-" + Guid.NewGuid().ToString("N")[..10], "/add"); // 14 characters: net asks nothing
        try
        {
            var sid = Profiles.SidOf(account) ?? throw new CheckFailed("konto sa nevytvorilo");
            Expect.True(!Profiles.IsAdministrator(account), "skúšobné konto nie je správca");
            Expect.True(Profiles.IsAdministrator(Environment.UserName), "konto runnera je správca");
            var path = new System.Text.StringBuilder(260);
            Expect.Equal(0, CreateProfile(sid, account, path, (uint)path.Capacity), "vytvorenie profilu");
            Expect.True(Profiles.State(sid).Exists, "profil existuje");

            var rule = new SessionElement { Id = "s", Account = account };
            var handler = new SessionRuleHandler();
            handler.Apply(rule);
            Expect.Equal(Observed.Present, handler.Observe(rule).State, "pravidlo zapísané");
            Expect.True(SessionRules.Read().ContainsKey(account), "pravidlo v registri");
            Expect.Throws<InvalidOperationException>(
                () =>
                {
                    if (OperatingSystem.IsWindows())
                    {
                        handler.Apply(new SessionElement { Id = "a", Account = Environment.UserName });
                    }
                },
                "správcovi pravidlo nedám");

            var cleaned = SessionCleaner.Clean(account, SessionCleaning.AtSignOut, DateTime.Now);
            Expect.True(cleaned.Cleaned, "vyčistené: " + cleaned.Message);
            Expect.True(!Profiles.State(sid).Exists && !Directory.Exists(path.ToString()), "profil aj priečinok zmizli");
            Expect.True(!SessionCleaner.Clean(account, SessionCleaning.AtSignOut, DateTime.Now).Cleaned, "druhýkrát niet čo čistiť");
            Expect.True(SessionCleaner.Clean(Environment.UserName, SessionCleaning.AtSignOut, DateTime.Now, force: true).Message.Contains("správca", StringComparison.Ordinal), "správcu nikdy");
        }
        finally
        {
            SessionRules.Set(account, null);
            Run("net.exe", "user", account, "/delete");
        }
    }

    [System.Runtime.InteropServices.DllImport("userenv.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CreateProfile(string sid, string userName, System.Text.StringBuilder profilePath, uint length);

    private static void Run(string program, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, program)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var p = System.Diagnostics.Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    private static void Sc(params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var p = System.Diagnostics.Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && args[0] != "delete")
        {
            throw new CheckFailed($"sc {string.Join(' ', args)} skončil kódom {p.ExitCode}");
        }
    }

    [Check]
    public static void TimeZoneIsSetAndBack()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var handler = new ComputerHandler();
        var original = Polaris.Windows.TimeZones.Current();
        var other = string.Equals(original, "Central Europe Standard Time", StringComparison.OrdinalIgnoreCase) ? "GMT Standard Time" : "Central Europe Standard Time";
        var element = new ComputerElement { Id = "c", TimeZone = other.ToLowerInvariant() };
        try
        {
            Polaris.Windows.Privileges.Enable(Polaris.Windows.TimeZones.TimeZonePrivilege);
        }
        catch (Polaris.Windows.SystemCallException e) when (e.Code == 1300)
        {
            // some hardened images take the right from administrators; the service of the agent runs as the system
            Console.WriteLine("  (konto nemá oprávnenie meniť časové pásmo – skúška vynechaná)");
            return;
        }

        try
        {
            Expect.Equal(Observed.Different, handler.Observe(element).State, "na začiatku iné pásmo");
            handler.Apply(element);
            Expect.Equal(other, Polaris.Windows.TimeZones.Current(), "pásmo nastavené a v presnom tvare Windows");
            Expect.Equal(Observed.Present, handler.Observe(element).State, "po zmene sedí (aj bez ohľadu na veľké písmená)");
            Expect.Throws<InvalidOperationException>(
                () =>
                {
                    if (OperatingSystem.IsWindows())
                    {
                        handler.Apply(new ComputerElement { Id = "z", TimeZone = "Polaris Neexistujuce Pasmo" });
                    }
                },
                "neznáme pásmo");
        }
        finally
        {
            Polaris.Windows.TimeZones.Set(original);
        }

        Expect.Equal(original, Polaris.Windows.TimeZones.Current(), "pôvodné pásmo späť");
    }

    [Check]
    public static void WorkgroupIsSetAndBack()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var (original, state) = Polaris.Windows.Workgroups.Current();
        if (state != Polaris.Windows.JoinState.Workgroup)
        {
            Console.WriteLine($"  (PC nie je v pracovnej skupine – stav {state}, skúška pracovnej skupiny vynechaná)");
            return;
        }

        var handler = new ComputerHandler();
        var element = new ComputerElement { Id = "c", Workgroup = "polarisskuska" };
        try
        {
            Expect.Equal(Observed.Different, handler.Observe(element).State, "na začiatku iná skupina");
            handler.Apply(element);
            var (now, after) = Polaris.Windows.Workgroups.Current();
            Expect.True(after == Polaris.Windows.JoinState.Workgroup && now == "POLARISSKUSKA", $"skupina je {now} ({after})");
            Expect.Equal(Observed.Present, handler.Observe(element).State, "po zmene sedí");
        }
        finally
        {
            Polaris.Windows.Workgroups.Join(original);
        }

        Expect.Equal(original, Polaris.Windows.Workgroups.Current().Name, "pôvodná skupina späť");
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
