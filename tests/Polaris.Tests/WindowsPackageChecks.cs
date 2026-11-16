using System.Security.Principal;
using Microsoft.Win32;
using Polaris.Agent;
using Polaris.Install;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>The whole road of a package on a real Windows: a real MSI built by own code, the publisher's signature, the
/// supply, the agent's checked copy, installation through Windows Installer, recognition by the product code, removal.
/// Only on Windows with the rights of an administrator (the CI runner).</summary>
public static class WindowsPackageChecks
{
    private static readonly DateTimeOffset T = new(2026, 11, 16, 22, 0, 0, TimeSpan.Zero);

    [Check]
    public static void PublisherSignaturesAreChecked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet))
        {
            return;
        }

        var signed = Authenticode.Check(dotnet);
        Expect.True(signed.Kind == SignatureKind.Valid && signed.Publisher is { Length: > 0 }, $"podpísaný: {signed.Kind} {signed.Publisher} {signed.Detail}");

        var dir = Directory.CreateTempSubdirectory("polaris-podpis-").FullName;
        try
        {
            var changed = Path.Combine(dir, "zmeneny.exe");
            var bytes = File.ReadAllBytes(dotnet);
            bytes[bytes.Length / 3] ^= 0xFF; // one byte inside the signed part
            File.WriteAllBytes(changed, bytes);
            var broken = Authenticode.Check(changed);
            Expect.Equal(SignatureKind.Invalid, broken.Kind, "zmenený súbor: " + broken.Detail);

            var plain = Path.Combine(dir, "obycajny.exe");
            File.WriteAllBytes(plain, [0x4D, 0x5A, 0, 0]);
            Expect.Equal(SignatureKind.Unsigned, Authenticode.Check(plain).Kind, "nepodpísaný súbor");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void AnMsiPackageGoesTheWholeRoad()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("polaris-msi-").FullName;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var key = $@"SOFTWARE\PolarisSkuska\msi-{tag}";
        var product = new MsiBuilder.Product("POLARIS skúška " + tag, "1.2.3", "POLARIS", Guid.NewGuid(), Guid.NewGuid(), key, "Hodnota", "ano");
        var msi = Path.Combine(dir, $"skuska-{tag}.msi");
        ProgramElement? element = null;
        ProgramHandler? handler = null;
        try
        {
            MsiBuilder.Build(msi, product);
            var facts = Msi.Read(msi);
            Expect.True(facts.ProductCode == "{" + product.ProductCode.ToString().ToUpperInvariant() + "}" && facts.ProductVersion == "1.2.3", "vlastnosti MSI: " + facts);
            Expect.Equal(SignatureKind.Unsigned, Authenticode.Check(msi).Kind, "vlastný balík nie je podpísaný");

            // the centre puts it into the supply and says what the recipe should pin
            var supply = Path.Combine(dir, "zasoba");
            var w = new StringWriter();
            var code = Polaris.Centrum.Commands.Run(["zasoba", "pridat", supply, msi, "--id", "skuska-" + tag], w, T.LocalDateTime, T);
            Expect.Equal(0, code, w.ToString());
            var package = new PackageStore(supply).Load("skuska-" + tag);
            Expect.True(package.Kind == PackageKind.Msi && package.ProductCode == facts.ProductCode && package.Publisher is null, "popis balíka MSI");

            element = new ProgramElement { Id = "skuska", Package = new PackageRef { Id = package.Id, Fingerprint = PackageFormat.Fingerprint(package) } };
            handler = new ProgramHandler(dir, supply);
            Expect.Equal(Observed.Absent, handler.Observe(element).State, "na začiatku nie je");

            using (var engine = new Engine([handler]))
            {
                var run = engine.Apply(engine.Plan([element]), null, () => T);
                Expect.True(run.Failed == 0, "inštalácia cez Windows Installer: " + RunReport.RunText(run));
            }

            Expect.True(Msi.State(facts.ProductCode).Installed, "Windows Installer pozná produkt");
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var value = root.OpenSubKey(key))
            {
                Expect.Equal("ano", value?.GetValue("Hodnota") as string, "balík zapísal svoju hodnotu");
            }

            Expect.True(File.Exists(Path.Combine(Packages.CacheFolder, package.Id, package.File)), "agent si nechal overenú kópiu");
            Expect.Equal(Observed.Present, handler.Observe(element).State, "rozpoznaný podľa ProductCode");

            // a description changed after the recipe was approved is refused
            var changed = new ProgramElement { Id = "skuska", Package = new PackageRef { Id = package.Id, Fingerprint = new string('0', 64) } };
            try
            {
                handler.Observe(changed);
                throw new CheckFailed("zmenený popis sa mal odmietnuť");
            }
            catch (InvalidDataException e)
            {
                Expect.True(e.Message.Contains("zmenil", StringComparison.Ordinal), e.Message);
            }

            element.Present = false;
            using (var engine = new Engine([handler]))
            {
                var run = engine.Apply(engine.Plan([element]), null, () => T);
                Expect.True(run.Failed == 0, "odinštalovanie: " + RunReport.RunText(run));
            }

            Expect.True(!Msi.State(facts.ProductCode).Installed, "produkt je preč");
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var value = root.OpenSubKey(key))
            {
                Expect.True(value?.GetValue("Hodnota") is null, "aj jeho hodnota");
            }
        }
        finally
        {
            if (element is { Present: true } && handler is not null && Msi.State("{" + product.ProductCode.ToString().ToUpperInvariant() + "}").Installed)
            {
                Msi.Remove("{" + product.ProductCode.ToString().ToUpperInvariant() + "}", null);
            }

            if (element?.Package is { } link)
            {
                var cached = Path.Combine(Packages.CacheFolder, link.Id);
                if (Directory.Exists(cached))
                {
                    Directory.Delete(cached, recursive: true);
                }
            }

            Directory.Delete(dir, recursive: true);
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
