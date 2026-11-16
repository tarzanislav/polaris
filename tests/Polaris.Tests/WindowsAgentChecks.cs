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
