using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Polaris.Core;
using Polaris.Install;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>Packages of the School supply on this PC: where the supply is, the agent's own protected copy of every package
/// it installs (so a package can still be removed when the supply is not reachable), and the checks before anything
/// runs: the fingerprint of the description pinned by the recipe, the fingerprint of the installer, the publisher.</summary>
[SupportedOSPlatform("windows")]
public static class Packages
{
    public const string SupplyValue = "Zasoba";
    public const string AgentKey = @"SOFTWARE\POLARIS\Agent";

    public static string AgentFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Product.Name, "agent");

    public static string CacheFolder => Path.Combine(AgentFolder, "balicky");

    public static string LogOf(string name) => Path.Combine(AgentFolder, "msi", name + ".log");

    /// <summary>The supply: given explicitly, set on this PC (polaris-agent zasoba), or "zasoba" next to the recipe.</summary>
    public static string? SupplyOf(string recipeFolder, string? supply)
    {
        if (supply is { Length: > 0 })
        {
            return supply;
        }

        if (Configured() is { } configured)
        {
            return configured;
        }

        var next = Path.Combine(recipeFolder, "zasoba");
        return Directory.Exists(next) ? next : null;
    }

    public static string? Configured()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(AgentKey);
        return key?.GetValue(SupplyValue) as string is { Length: > 0 } path ? path : null;
    }

    public static void Configure(string? path)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(AgentKey, writable: true);
        if (path is null)
        {
            key.DeleteValue(SupplyValue, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(SupplyValue, path, RegistryValueKind.String);
        }
    }

    /// <summary>The description pinned by the recipe: from the supply, or – when the supply is out of reach – the agent's
    /// own copy of the very same description.</summary>
    public static Package Resolve(PackageRef link, string recipeFolder, string? supply)
    {
        var cached = Path.Combine(CacheFolder, link.Id, PackageFormat.FileName);
        Exception? failure = null;
        if (SupplyOf(recipeFolder, supply) is { } root)
        {
            try
            {
                return new PackageStore(root).Load(link.Id, link.Fingerprint);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failure = e; // the share is not reachable now
            }
        }

        if (File.Exists(cached))
        {
            var p = PackageFormat.FromJson(File.ReadAllText(cached, Encoding.UTF8));
            if (p.Id == link.Id && string.Equals(PackageFormat.Fingerprint(p), link.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }

        throw failure ?? new FileNotFoundException(
            $"Balík {link.Id}: zásoba nie je nastavená (polaris-agent zasoba CESTA) ani vedľa receptu (priečinok zasoba).");
    }

    /// <summary>The installer in the agent's protected folder, checked: fingerprint, size and – when the description names
    /// one – the publisher's valid signature. Only this copy ever runs.</summary>
    public static string Fetch(Package p, string recipeFolder, string? supply)
    {
        var folder = Path.Combine(CacheFolder, p.Id);
        var local = Path.Combine(folder, p.File);
        if (!(File.Exists(local) && new FileInfo(local).Length == p.Size && RecipeText.FileFingerprint(local) == p.Sha256))
        {
            var root = SupplyOf(recipeFolder, supply) ?? throw new FileNotFoundException($"Balík {p.Id}: zásoba nie je dostupná.");
            var source = new PackageStore(root).InstallerOf(p);
            Directory.CreateDirectory(folder);
            AgentService.ProtectFolder(AgentFolder);
            var partial = local + ".kopia";
            File.Copy(source, partial, overwrite: true);
            var actual = RecipeText.FileFingerprint(partial);
            if (actual != p.Sha256)
            {
                File.Delete(partial);
                throw new InvalidDataException($"Inštalátor balíka {p.Id} v zásobe má iný odtlačok, než je v jeho popise – nespustil som ho.");
            }

            File.Move(partial, local, overwrite: true);
            File.WriteAllText(Path.Combine(folder, PackageFormat.FileName), PackageFormat.ToJson(p), new UTF8Encoding(false));
        }

        if (p.Publisher is { Length: > 0 } publisher)
        {
            var signature = Authenticode.Check(local);
            if (signature.Kind != SignatureKind.Valid || !string.Equals(signature.Publisher, publisher, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Balík {p.Id}: podpis vydavateľa {publisher} nesedí ({signature.Detail}{(signature.Publisher is { } s ? ", podpísal " + s : "")}) – nespustil som ho.");
            }
        }

        return local;
    }
}
