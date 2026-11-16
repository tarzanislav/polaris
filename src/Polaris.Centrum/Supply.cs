using System.Diagnostics;
using Polaris.Install;
using Polaris.Recipes;

namespace Polaris.Centrum;

/// <summary>The School supply from the owner's side: adding installers, listing, checking.</summary>
public static partial class Commands
{
    private static int Supply(List<string> words, TextWriter o, DateTimeOffset utcNow)
    {
        if (words.Count < 3 || words[1] is not ("pridat" or "zoznam" or "overit"))
        {
            throw new UsageException("Použitie: zasoba pridat ZÁSOBA SÚBOR [voľby] | zasoba zoznam ZÁSOBA | zasoba overit ZÁSOBA");
        }

        var store = new PackageStore(words[2]);
        switch (words[1])
        {
            case "zoznam":
                var any = false;
                foreach (var id in store.Ids())
                {
                    any = true;
                    try
                    {
                        var p = store.Load(id);
                        o.WriteLine($"{p.Id,-32} {p.Kind,-3} {p.Version,-14} {Size(p.Size),9}  {p.Publisher ?? "nepodpísaný"}  {PackageFormat.Fingerprint(p)[..12]}…");
                    }
                    catch (Exception e) when (e is InvalidDataException or IOException)
                    {
                        o.WriteLine($"{id,-32} CHYBA: {e.Message}");
                    }
                }

                if (!any)
                {
                    o.WriteLine("Zásoba je prázdna.");
                }

                return 0;
            case "overit":
                var problems = store.Verify();
                foreach (var p in problems)
                {
                    o.WriteLine("  – " + p);
                }

                o.WriteLine(problems.Count == 0 ? $"Zásoba je v poriadku ({store.Ids().Count()} balíkov)." : $"Problémy: {problems.Count}.");
                return problems.Count == 0 ? 0 : 1;
        }

        return AddPackage(store, words.Skip(3).ToList(), o, utcNow);
    }

    private static int AddPackage(PackageStore store, List<string> rest, TextWriter o, DateTimeOffset utcNow)
    {
        string? Option(string name)
        {
            var at = rest.IndexOf(name);
            if (at < 0)
            {
                return null;
            }

            if (at + 1 >= rest.Count)
            {
                throw new UsageException($"Po {name} chýba hodnota.");
            }

            var value = rest[at + 1];
            rest.RemoveRange(at, 2);
            return value;
        }

        var id = Option("--id");
        var title = Option("--nazov");
        var version = Option("--verzia");
        var arguments = Option("--argumenty") ?? "";
        var uninstall = Option("--odinstalovanie");
        var displayName = Option("--meno") ?? "";
        var note = Option("--poznamka") ?? "";
        var prefix = rest.Remove("--zaciatok");
        if (rest.Count != 1)
        {
            throw new UsageException("Použitie: zasoba pridat ZÁSOBA SÚBOR [voľby] – neznáme slová: " + string.Join(" ", rest.Skip(1)));
        }

        var file = rest[0];
        if (!File.Exists(file))
        {
            throw new UsageException($"Súbor {file} neexistuje.");
        }

        var p = new Package
        {
            Arguments = arguments,
            UninstallArguments = uninstall,
            DisplayName = displayName,
            Match = prefix ? NameMatch.Prefix : NameMatch.Exact,
            AddedUtc = utcNow,
            Note = note,
        };
        var extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension == ".msi")
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new UsageException("Balík MSI číta Windows Installer – pridaj ho vo Windows.");
            }

            MsiFacts facts;
            try
            {
                facts = Msi.Read(file);
            }
            catch (InvalidOperationException e)
            {
                throw new UsageException("Súbor nie je platný balík MSI: " + e.Message);
            }

            p.Kind = PackageKind.Msi;
            p.ProductCode = facts.ProductCode;
            p.UpgradeCode = facts.UpgradeCode.Length > 0 ? facts.UpgradeCode : null;
            p.Title = title ?? facts.ProductName;
            p.Version = version ?? facts.ProductVersion;
            p.DisplayName = displayName.Length > 0 ? displayName : facts.ProductName;
        }
        else if (extension == ".exe")
        {
            p.Kind = PackageKind.Exe;
            p.Title = title ?? Path.GetFileNameWithoutExtension(file);
            p.Version = version ?? (FileVersionInfo.GetVersionInfo(file).ProductVersion ?? "").Trim();
        }
        else
        {
            throw new UsageException("Zásoba prijme inštalátor .msi alebo .exe.");
        }

        if (OperatingSystem.IsWindows())
        {
            var signature = Authenticode.Check(file);
            if (signature.Kind == SignatureKind.Invalid)
            {
                throw new UsageException($"Inštalátor má neplatný podpis ({signature.Detail}) – do zásoby ho nepridám.");
            }

            p.Publisher = signature.Publisher;
        }

        p.Id = id ?? PackageFormat.SuggestId(p.Title, p.Version);
        try
        {
            store.Add(file, p);
        }
        catch (InvalidDataException e)
        {
            throw new UsageException(e.Message);
        }

        o.WriteLine($"Balík {p.Id} je v zásobe: {p.Title} {p.Version} ({p.Kind}, {Size(p.Size)}), {(p.Publisher is { } s ? "podpísal " + s : "bez podpisu vydavateľa – chráni ho len odtlačok")}.");
        if (p.Publisher is null && !OperatingSystem.IsWindows())
        {
            o.WriteLine("Podpis vydavateľa sa dá overiť len vo Windows; agent overí odtlačok inštalátora pri každej inštalácii.");
        }

        o.WriteLine("Do receptu (prvok program):");
        o.WriteLine($"  \"package\": {PackageStore.RecipeSnippet(p)}");
        return 0;
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} kB",
        _ => $"{bytes} B",
    };
}
