using System.Globalization;
using System.Text.RegularExpressions;

namespace Polaris.Recipes;

/// <summary>Data of registry values: from the text in a recipe to the value written, one rule for the check and the agent.</summary>
public static class RegistryData
{
    /// <summary>uint for DWord, ulong for QWord, string[] for MultiString, string otherwise; null when the text is not valid.</summary>
    public static object? Parse(RegistryKind kind, string data)
    {
        switch (kind)
        {
            case RegistryKind.DWord:
                return ParseNumber(data, uint.MaxValue) is { } d ? (uint)d : null;
            case RegistryKind.QWord:
                return ParseNumber(data, ulong.MaxValue);
            case RegistryKind.MultiString:
                return data.Length == 0 ? Array.Empty<string>() : data.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            default:
                return data;
        }
    }

    /// <summary>The same value in the text form of a recipe (for showing what is there now).</summary>
    public static string Show(object? value) => value switch
    {
        null => "(nič)",
        string[] lines => string.Join(" | ", lines),
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static ulong? ParseNumber(string text, ulong max)
    {
        var t = text.Trim();
        var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(t.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n)
            : ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        return ok && n <= max ? n : null;
    }
}

/// <summary>Problems of a recipe in Slovak sentences, before anything touches a PC. A recipe with problems is never
/// applied.</summary>
public static partial class RecipeCheck
{
    /// <summary>Parts of the registry a recipe must not change through a plain value: a mistake there could stop Windows
    /// from starting or anybody from signing in, or it would touch POLARIS itself.</summary>
    public static readonly IReadOnlyList<string> ProtectedRegistry =
    [
        @"HKLM\SAM",
        @"HKLM\SECURITY",
        @"HKLM\BCD00000000",
        @"HKLM\SOFTWARE\POLARIS",
        @"HKLM\SYSTEM\CurrentControlSet\Services",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa",
        @"HKLM\SYSTEM\Setup",
        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
    ];

    public const int MaxBookmarks = 500;
    public const int MaxBookmarkDepth = 4;

    public static List<string> Problems(Recipe recipe)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(recipe.Name))
        {
            problems.Add("Recept nemá meno profilu.");
        }

        if (recipe.Version < 0)
        {
            problems.Add("Číslo verzie nesmie byť záporné.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in recipe.Elements)
        {
            var label = element.Id.Length > 0 ? element.Id : "(bez mena)";
            if (!IdPattern().IsMatch(element.Id))
            {
                problems.Add($"Prvok {label}: meno smie mať len malé písmená bez diakritiky, číslice, '-', '.', '_' (najviac 64 znakov).");
            }
            else if (!ids.Add(element.Id))
            {
                problems.Add($"Meno prvku {element.Id} je v recepte dvakrát.");
            }

            foreach (var p in ElementProblems(element))
            {
                problems.Add($"Prvok {label}: {p}");
            }
        }

        // only when every element is fine on its own: two elements must not manage the same thing
        if (problems.Count == 0)
        {
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var element in recipe.Elements)
            {
                foreach (var simple in Expansion.Expand(element))
                {
                    if (owners.TryGetValue(simple.Key, out var first) && first != element.Id)
                    {
                        problems.Add($"Prvky {first} a {element.Id} menia to isté ({simple.Title}).");
                    }
                    else
                    {
                        owners[simple.Key] = element.Id;
                    }
                }
            }
        }

        return problems;
    }

    public static IEnumerable<string> ElementProblems(Element element) => element switch
    {
        ProgramElement p => ProgramProblems(p),
        RegistryValueElement r => RegistryProblems(r),
        BookmarksElement b => BookmarkProblems(b),
        _ => ["neznámy typ prvku."],
    };

    private static IEnumerable<string> ProgramProblems(ProgramElement p)
    {
        if (string.IsNullOrWhiteSpace(p.DisplayName))
        {
            yield return "chýba displayName – meno, pod ktorým program vidno v zozname nainštalovaných programov.";
        }
        else if (p.Match == NameMatch.Prefix && p.DisplayName.Trim().Length < 3)
        {
            yield return "začiatok mena (match: Prefix) musí mať aspoň 3 znaky, inak by sa pomýlil s inými programami.";
        }

        var sources = (p.Winget is { Length: > 0 } ? 1 : 0) + (p.Installer is not null ? 1 : 0);
        if (p.Present && sources != 1)
        {
            yield return "program na inštaláciu potrebuje práve jeden zdroj: winget, alebo installer.";
        }
        else if (sources > 1)
        {
            yield return "program má aj winget, aj installer – zvoľ jeden.";
        }

        if (p.Winget is { Length: > 0 } id && !WingetPattern().IsMatch(id))
        {
            yield return $"„{id}“ nie je platný balík WinGet (tvar Vydavateľ.Názov).";
        }

        if (p.Version is { Length: > 0 } v && !VersionPattern().IsMatch(v))
        {
            yield return $"verzia „{v}“ má neplatné znaky.";
        }

        if (p.Version is { Length: > 0 } && p.Winget is not { Length: > 0 })
        {
            yield return "verzia sa dá zvoliť len pri programe z WinGet.";
        }

        if (p.Installer is { } i)
        {
            if (string.IsNullOrWhiteSpace(i.File) || Path.IsPathRooted(i.File) || i.File.Replace('\\', '/').Split('/').Contains(".."))
            {
                yield return "installer.file musí byť cesta vnútri priečinka receptu (napr. subory/program.msi).";
            }

            if (!Sha256Pattern().IsMatch(i.Sha256))
            {
                yield return "installer.sha256 musí byť odtlačok SHA-256 (64 znakov 0-9 a-f).";
            }
        }
    }

    private static IEnumerable<string> RegistryProblems(RegistryValueElement r)
    {
        var under = RegistryPath.UnderLocalMachine(r.Path);
        if (under is null || under.Length == 0)
        {
            yield return "cesta registra musí začínať HKLM\\ a pokračovať kľúčom (recept mení len časť počítača).";
            yield break;
        }

        var normal = RegistryPath.Normalize(r.Path);
        foreach (var root in ProtectedRegistry)
        {
            if (normal.Equals(root, StringComparison.OrdinalIgnoreCase)
                || normal.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"{root} je chránená časť registra – chyba by mohla zablokovať štart alebo prihlásenie.";
                yield break;
            }
        }

        if (r.Present && RegistryData.Parse(r.Kind, r.Data) is null)
        {
            yield return r.Kind == RegistryKind.DWord
                ? $"„{r.Data}“ nie je číslo DWord (0 až 4294967295, alebo 0x…)."
                : $"„{r.Data}“ nie je číslo QWord.";
        }
    }

    private static IEnumerable<string> BookmarkProblems(BookmarksElement b)
    {
        if (b.Browsers.Count == 0)
        {
            yield return "záložky nemajú žiadny prehliadač.";
        }

        if (b.Present && b.Items.Count == 0)
        {
            yield return "záložky sú prázdne.";
        }

        var count = 0;
        foreach (var p in BookmarkItemProblems(b.Items, 1, () => count++))
        {
            yield return p;
        }

        if (count > MaxBookmarks)
        {
            yield return $"záložiek je {count}, najviac sa dá {MaxBookmarks}.";
        }
    }

    private static IEnumerable<string> BookmarkItemProblems(List<Bookmark> items, int depth, Action counter)
    {
        if (depth > MaxBookmarkDepth)
        {
            yield return $"priečinky záložiek sú vnorené hlbšie ako {MaxBookmarkDepth} úrovne.";
            yield break;
        }

        foreach (var item in items)
        {
            counter();
            var name = item.Name.Length > 0 ? item.Name : "(bez mena)";
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                yield return "záložka bez mena.";
            }

            if (item.Children is { } children)
            {
                if (item.Url is not null)
                {
                    yield return $"„{name}“ je priečinok aj záložka naraz – zvoľ url, alebo children.";
                }

                foreach (var p in BookmarkItemProblems(children, depth + 1, counter))
                {
                    yield return p;
                }
            }
            else if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                yield return $"„{name}“ nemá platnú adresu http alebo https.";
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_+]*(\.[A-Za-z0-9\-_+]+)+$")]
    private static partial Regex WingetPattern();

    [GeneratedRegex(@"^[0-9A-Za-z.\-_+]{1,64}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();
}
