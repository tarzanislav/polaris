using System.Security.Principal;
using Polaris.Install;

namespace Polaris.Tests;

/// <summary>TEMPORARY: finds out which property of the self-built package Windows Installer refuses.</summary>
public static class WindowsMsiProbe
{
    [Check]
    public static void Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("polaris-probe-").FullName;
        var report = new List<string>();
        foreach (var (label, name) in new[] { ("ascii", "POLARIS probe"), ("diakritika", "POLARIS skúška") })
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            var product = new MsiBuilder.Product(name + " " + tag, "1.0.0", "POLARIS", Guid.NewGuid(), Guid.NewGuid(), $@"SOFTWARE\PolarisSkuska\probe-{tag}", "Hodnota", "ano");
            var msi = Path.Combine(dir, $"{label}.msi");
            MsiBuilder.Build(msi, product);
            var log = Path.Combine(dir, label + ".log");
            var r = Msi.Install(msi, "", log);
            report.Add($"{label}: {r.Code} {Msi.Why(log)}");
            if (r.Ok)
            {
                var code = "{" + product.ProductCode.ToString().ToUpperInvariant() + "}";
                report.Add($"  odstránenie: {Msi.Remove(code, null).Code}");
            }
            else if (File.Exists(log))
            {
                var lines = File.ReadAllLines(log);
                var at = Array.FindIndex(lines, l => l.Contains("Action start", StringComparison.Ordinal) && l.Contains("RegisterProduct", StringComparison.Ordinal));
                if (at >= 0)
                {
                    report.Add("  " + string.Join(" ¦ ", lines.Skip(at).Take(25).Select(l => l.Length > 160 ? l[..160] : l)));
                }
            }
        }

        throw new CheckFailed("SONDA MSI: " + string.Join(" || ", report));
    }
}
