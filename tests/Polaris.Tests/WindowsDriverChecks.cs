using System.Runtime.Versioning;
using Polaris.Agent;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>Drivers on a real Windows (the CI runner): devices through the configuration manager, a backup of the
/// runner's own driver packages and installing one of them again from the backup. Elsewhere they pass doing nothing.</summary>
public static class WindowsDriverChecks
{
    [Check]
    public static void DevicesAreReadWithTheirDrivers()
    {
        if (OperatingSystem.IsWindows())
        {
            Devices();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Devices()
    {
        var devices = Polaris.Windows.Devices.Present();
        Expect.True(devices.Count > 10, $"PC má zariadenia ({devices.Count})");
        Expect.True(devices.All(d => d.InstanceId.Length > 0 && d.Name.Length > 0), "každé má identifikátor a meno");
        Expect.True(devices.Any(d => d.HardwareIds.Count > 0), "identifikátory hardvéru");
        var withDriver = devices.Where(d => d.InfPath is { Length: > 0 }).ToList();
        Expect.True(withDriver.Count > 0 && withDriver.Any(d => d.DriverVersion is { Length: > 0 } && d.DriverDate is not null), "ovládač s verziou a dátumom");
        Console.WriteLine($"  ({devices.Count} zariadení, {devices.Count(d => d.MissingDriver)} bez ovládača, {Polaris.Windows.DriverStore.ThirdParty().Count} balíkov tretích strán)");
    }

    [Check]
    public static void ABackupOfThisPcIsVerifiedAndInstalledAgain()
    {
        if (OperatingSystem.IsWindows() && IsAdmin())
        {
            BackupAndInstall();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void BackupAndInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "polaris-zaloha-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backup = DriverWork.Backup(root, "RUNNER", all: true, TextWriter.Null);
            Console.WriteLine($"  (záloha: {backup.Packages.Count} balíkov, podpísaných {backup.Packages.Count(p => p.Signer.Length > 0)})");
            var read = DriverBackupFormat.Load(root);
            Expect.Equal(DriverBackupFormat.Fingerprint(backup), DriverBackupFormat.Fingerprint(read), "popis zálohy na disku");
            Expect.Equal(0, DriverBackupFormat.VerifyFiles(root, read).Count, "všetky súbory zálohy sedia: " + string.Join(" ", DriverBackupFormat.VerifyFiles(root, read)));
            Expect.Throws<InvalidDataException>(() => DriverWork.Backup(root, "ZNOVA", all: true, TextWriter.Null), "do neprázdneho priečinka nie");

            var used = DriverWork.Needed(read, Polaris.Windows.Devices.Present(), all: false);
            Expect.True(used.Count <= read.Packages.Count, "potrebné sú podmnožinou");
            if (read.Packages.FirstOrDefault(p => p.Signer.Length > 0 && used.Contains(p)) is not { } package)
            {
                Console.WriteLine("  (na runneri nie je podpísaný balík tretej strany v používaní – inštalácia vynechaná)");
                return;
            }

            var result = DriverWork.Install(root, package);
            Expect.True(result.Ok, $"{package.Folder} nainštalovaný znova zo zálohy: {result.Message}");

            // a changed file is refused before Windows sees it
            var file = package.Files.First(f => !string.Equals(f.Path, package.Catalog, StringComparison.OrdinalIgnoreCase));
            File.AppendAllText(Path.Combine(root, package.Folder, file.Path.Replace('/', '\\')), "x");
            var refused = DriverWork.Install(root, package);
            Expect.True(!refused.Ok && refused.Message.Contains("zmenený", StringComparison.Ordinal), "zmenený súbor odmietnutý: " + refused.Message);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
