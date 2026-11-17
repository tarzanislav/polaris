using System.Globalization;
using System.Text.RegularExpressions;

namespace Polaris.Recipes;

/// <summary>Data of registry values: from the text in a recipe to the value written, one rule for the check and the agent.</summary>
public static class RegistryData
{
    /// <summary>The longest binary value a recipe may write.</summary>
    public const int MaxBinary = 4096;

    /// <summary>uint for DWord, ulong for QWord, string[] for MultiString, byte[] for Binary, string otherwise; null when
    /// the text is not valid.</summary>
    public static object? Parse(RegistryKind kind, string data)
    {
        switch (kind)
        {
            case RegistryKind.Binary:
                return ParseBytes(data);
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

    /// <summary>Hexadecimal pairs; spaces, commas and new lines between the pairs are allowed, never inside one.</summary>
    private static byte[]? ParseBytes(string text)
    {
        var bytes = new List<byte>(text.Length / 2);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is ' ' or ',' or '\n' or '\r' or '\t')
            {
                i++;
                continue;
            }

            if (i + 1 >= text.Length || !char.IsAsciiHexDigit(c) || !char.IsAsciiHexDigit(text[i + 1]) || bytes.Count == MaxBinary)
            {
                return null;
            }

            bytes.Add((byte)((HexValue(c) << 4) | HexValue(text[i + 1])));
            i += 2;
        }

        return bytes.ToArray();
    }

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

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

    /// <summary>Services a recipe must not disable or stop: without them Windows, its protection or POLARIS stop working.</summary>
    public static readonly IReadOnlySet<string> ProtectedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "WdNisSvc", "SecurityHealthService", "Sense", "mpssvc", "BFE", "RpcSs", "RpcEptMapper", "DcomLaunch",
        "EventLog", "LSM", "SamSs", "Winmgmt", "CryptSvc", "TrustedInstaller", "Schedule", "ProfSvc", "gpsvc", "Power",
        "PlugPlay", "UserManager", "Dhcp", "Dnscache", "nsi", "BrokerInfrastructure", "CoreMessagingRegistrar", "wuauserv",
        "WaaSMedicSvc", "PolarisAgent", "DWAgent", "SgrmBroker", "WdFilter", "WdBoot",
    };

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

        // a program of the store must not be a permanent program of the recipe – removing it after a session would
        // remove the permanent one
        var permanent = recipe.Elements.OfType<ProgramElement>().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var item in recipe.Elements.OfType<StoreElement>().SelectMany(s => s.Items).Where(i => permanent.Contains(i.Program.Key)))
        {
            problems.Add($"Program {item.Program.DisplayName} je v recepte natrvalo aj v Školskom obchode – nechaj ho len na jednom mieste.");
        }

        if (recipe.Elements.OfType<AccountElement>().Count(a => a.AutoLogon) > 1)
        {
            problems.Add("Automaticky sa PC môže prihlásiť len do jedného konta.");
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
        ServiceElement v => ServiceProblems(v),
        FeatureElement f => FeatureProblems(f),
        UserRegistryElement u => UserRegistryProblems(u),
        KeyboardsElement k => KeyboardProblems(k),
        SessionElement s => SessionProblems(s),
        SandboxElement x => SandboxProblems(x),
        StoreElement o => StoreProblems(o),
        ComputerElement c => ComputerProblems(c),
        UpdatesElement u => UpdatesProblems(u),
        PowerElement w => PowerProblems(w),
        SoundsElement z => z.Present ? [] : ["zvuky sa receptom neodstraňujú – vynechaj prvok, ak má rozhodnúť Windows."],
        StartupElement t => StartupRules.Problems(t),
        AccountElement a => AccountRules.Problems(a),
        _ => ["neznámy typ prvku."],
    };

    private static IEnumerable<string> ProgramProblems(ProgramElement p)
    {
        if (string.IsNullOrWhiteSpace(p.DisplayName))
        {
            if (p.Package is null)
            {
                yield return "chýba displayName – meno, pod ktorým program vidno v zozname nainštalovaných programov.";
            }
        }
        else if (p.Match == NameMatch.Prefix && p.DisplayName.Trim().Length < 3)
        {
            yield return "začiatok mena (match: Prefix) musí mať aspoň 3 znaky, inak by sa pomýlil s inými programami.";
        }

        var sources = (p.Winget is { Length: > 0 } ? 1 : 0) + (p.Installer is not null ? 1 : 0) + (p.Package is not null ? 1 : 0);
        if (sources > 1)
        {
            yield return "program má viac zdrojov (winget, installer, package) – zvoľ jeden.";
        }
        else if (p.Present && sources == 0)
        {
            yield return "program na inštaláciu potrebuje zdroj: winget, installer alebo package (balík zo zásoby).";
        }

        if (p.Package is { } package)
        {
            if (!PackageFormat.IdPattern().IsMatch(package.Id))
            {
                yield return $"package.id „{package.Id}“ nie je platné id balíka.";
            }

            if (!Sha256Pattern().IsMatch(package.Fingerprint))
            {
                yield return "package.fingerprint musí byť odtlačok popisu balíka (64 znakov 0-9 a-f; vypíše ho polaris-centrum zasoba pridat).";
            }
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
            yield return BadData(r.Kind, r.Data);
        }
    }

    private static IEnumerable<string> ComputerProblems(ComputerElement c)
    {
        if (!c.Present)
        {
            yield return "nastavenia počítača sa neodstraňujú – vynechaj prvok alebo pole, ktoré nemá meniť.";
        }

        if (c.Workgroup is null && c.TimeZone is null)
        {
            yield return "prvok computer nič nenastavuje – vyplň workgroup alebo timeZone.";
        }

        if (c.Workgroup is { } w && !WorkgroupPattern().IsMatch(w))
        {
            yield return $"„{w}“ nie je platná pracovná skupina (1 až 15 znakov A-Z, 0-9, '-', '_', nie len číslice).";
        }

        if (c.TimeZone is { } t && !TimeZonePattern().IsMatch(t))
        {
            yield return $"„{t}“ nie je meno časového pásma Windows (napr. Central Europe Standard Time).";
        }
    }

    private static IEnumerable<string> UpdatesProblems(UpdatesElement u)
    {
        if ((u.ActiveHoursStart is null) != (u.ActiveHoursEnd is null))
        {
            yield return "aktívne hodiny potrebujú oba konce: activeHoursStart aj activeHoursEnd.";
        }
        else if (u.ActiveHoursStart is { } start && u.ActiveHoursEnd is { } end)
        {
            if (start is < 0 or > 23 || end is < 0 or > 23)
            {
                yield return "hodiny aktívneho času sú 0 až 23.";
            }
            else if (start == end || (end - start + 24) % 24 > SettingsExpansion.MaxActiveHours)
            {
                yield return $"aktívne hodiny musia trvať 1 až {SettingsExpansion.MaxActiveHours} hodín (obmedzenie Windows).";
            }
        }

        if (u.DeferFeatureDays is < 0 or > SettingsExpansion.MaxDeferFeatureDays)
        {
            yield return $"deferFeatureDays je 0 až {SettingsExpansion.MaxDeferFeatureDays} dní.";
        }

        if (u.DeferQualityDays is < 0 or > SettingsExpansion.MaxDeferQualityDays)
        {
            yield return $"deferQualityDays je 0 až {SettingsExpansion.MaxDeferQualityDays} dní.";
        }

        if (u.TargetRelease is { } r && !ReleasePattern().IsMatch(r))
        {
            yield return $"„{r}“ nie je verzia Windows 11 (tvar 25H2).";
        }
    }

    private static IEnumerable<string> PowerProblems(PowerElement p)
    {
        foreach (var (name, minutes) in new[] { ("sleepMinutes", p.SleepMinutes), ("displayMinutes", p.DisplayMinutes), ("hibernateMinutes", p.HibernateMinutes) })
        {
            if (minutes is < 0 or > SettingsExpansion.MaxMinutes)
            {
                yield return $"{name} je 0 (nikdy) až {SettingsExpansion.MaxMinutes} minút.";
            }
        }

        if (p.Present && p.SleepMinutes is null && p.DisplayMinutes is null && p.HibernateMinutes is null && p.PowerButton is null
            && p.LidClose is null && p.WakePassword is null && p.FastStartup is null && p.Hibernation is null)
        {
            yield return "prvok power nič nenastavuje.";
        }
    }

    private static string BadData(RegistryKind kind, string data) => kind switch
    {
        RegistryKind.DWord => $"„{data}“ nie je číslo DWord (0 až 4294967295, alebo 0x…).",
        RegistryKind.QWord => $"„{data}“ nie je číslo QWord.",
        RegistryKind.Binary => $"„{Short(data)}“ nie sú bajty Binary (dvojice 0-9 A-F, najviac {RegistryData.MaxBinary} bajtov).",
        _ => $"„{Short(data)}“ nie sú platné údaje pre {kind}.",
    };

    private static string Short(string text) => text.Length <= 40 ? text : text[..37] + "…";

    private static IEnumerable<string> ServiceProblems(ServiceElement v)
    {
        if (!SystemNamePattern().IsMatch(v.Prefix) || v.Name.Trim().Count(c => c == '*') > 1 || (v.IsPrefix && v.Prefix.Length < 4))
        {
            yield return "chýba alebo je neplatné meno služby (meno kľúča služby, napr. Spooler; začiatok mena s * na konci aspoň 4 znaky).";
        }

        if (v.IsPrefix && v.Start != ServiceStart.Disabled && v.Start != ServiceStart.Manual)
        {
            yield return "služby podľa začiatku mena sa dajú len vypnúť alebo prepnúť na manuálne.";
        }

        if (v.IsPrefix && ProtectedServices.Any(p => p.StartsWith(v.Prefix, StringComparison.OrdinalIgnoreCase)))
        {
            yield return $"začiatok mena {v.Prefix} zahŕňa chránenú službu.";
        }

        if (!v.Present)
        {
            yield return "službu recept neodstraňuje – na vypnutie nastav start: Disabled.";
        }

        if (ProtectedServices.Contains(v.Name.Trim()) && (v.Start == ServiceStart.Disabled || v.Running == false))
        {
            yield return $"služba {v.Name} je chránená – bez nej by nefungoval Windows, jeho ochrana alebo POLARIS.";
        }

        if (v.Start == ServiceStart.Disabled && v.Running == true)
        {
            yield return "vypnutá služba nemôže bežať.";
        }
    }

    private static IEnumerable<string> FeatureProblems(FeatureElement f)
    {
        if (!SystemNamePattern().IsMatch(f.Name))
        {
            yield return "chýba alebo je neplatné meno súčasti Windows (napr. Containers-DisposableClientVM).";
        }
    }

    private static IEnumerable<string> UserRegistryProblems(UserRegistryElement u)
    {
        var path = UserRegistryPath.Normalize(u.Path);
        if (path.Length == 0 || path.StartsWith("HK", StringComparison.OrdinalIgnoreCase))
        {
            yield return "cesta je v časti registra používateľa bez HKCU\\ (napr. Control Panel\\Desktop).";
        }

        if (u.Present && RegistryData.Parse(u.Kind, u.Data) is null)
        {
            yield return BadData(u.Kind, u.Data);
        }
    }

    private static IEnumerable<string> KeyboardProblems(KeyboardsElement k)
    {
        if (k.Languages.Count == 0 || k.Languages.Count > KeyboardLayouts.MaxLanguages)
        {
            yield return $"klávesníc musí byť 1 až {KeyboardLayouts.MaxLanguages}.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in k.Languages)
        {
            if (KeyboardLayouts.Of(tag, k.Layouts) is not { } pair)
            {
                yield return $"jazyk {tag} nepoznám – doplň layouts: \"{tag}\": \"LANGID:ROZLOŽENIE\" (napr. \"0422:00020422\").";
            }
            else if (!ids.Add(pair.LangId))
            {
                yield return $"jazyk s identifikátorom {pair.LangId} je v zozname dvakrát (jeden jazyk = jedna klávesnica).";
            }
        }

        foreach (var (tag, layout) in k.Layouts ?? [])
        {
            if (!k.Languages.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                yield return $"layouts uvádza jazyk {tag}, ktorý nie je v languages.";
            }
            else if (!KeyboardLayouts.FullPattern().IsMatch(layout) && !KeyboardLayouts.LayoutPattern().IsMatch(layout))
            {
                yield return $"rozloženie „{layout}“ musí mať tvar 0001041B alebo 041B:0001041B.";
            }
        }
    }

    private static IEnumerable<string> SessionProblems(SessionElement s)
    {
        var account = s.Account.Trim();
        if (!AccountPattern().IsMatch(account))
        {
            yield return "chýba alebo je neplatné meno konta (lokálne konto, najviac 20 znakov).";
        }
        else if (SessionPolicy.NeverClean.Contains(account))
        {
            yield return $"konto {account} je systémové alebo správcovské – jeho profil sa nikdy nemaže.";
        }
    }

    private static IEnumerable<string> SandboxProblems(SandboxElement x)
    {
        if (!SandboxNamePattern().IsMatch(x.Name.Trim()))
        {
            yield return "šablóna Pieskoviska potrebuje meno (písmená, číslice, medzera, -, najviac 40 znakov).";
        }

        if (x.MemoryMb < SandboxConfig.MinMemory || x.MemoryMb > SandboxConfig.MaxMemory)
        {
            yield return $"pamäť Pieskoviska musí byť {SandboxConfig.MinMemory} až {SandboxConfig.MaxMemory} MB.";
        }

        foreach (var f in x.Folders)
        {
            var host = f.HostFolder.Trim().TrimEnd('\\');
            if (!WindowsPath().IsMatch(f.HostFolder) || host.Split('\\').Contains(".."))
            {
                yield return $"priečinok PC „{f.HostFolder}“ musí byť celá cesta (napr. C:\\Users\\Public\\Documents).";
            }
            else if (!f.ReadOnly && SystemFolder(host))
            {
                yield return $"priečinok PC „{f.HostFolder}“ je systémový – do Pieskoviska ho možno dať len na čítanie.";
            }

            if (f.SandboxFolder is { Length: > 0 } inside && !WindowsPath().IsMatch(inside))
            {
                yield return $"priečinok v Pieskovisku „{inside}“ musí byť celá cesta (napr. C:\\Users\\WDAGUtilityAccount\\Desktop\\Ucivo).";
            }
        }

        if (x.LogonCommand is { Length: > 1000 })
        {
            yield return "príkaz po štarte je dlhší ako 1000 znakov.";
        }
    }

    /// <summary>The disk root and the folders of Windows and programs: never writable from inside the Sandbox.</summary>
    private static bool SystemFolder(string host)
    {
        var rest = host.Length > 2 ? host[3..] : "";
        var top = rest.Split('\\')[0];
        return rest.Length == 0 || top.Equals("Windows", StringComparison.OrdinalIgnoreCase) || top.StartsWith("Program", StringComparison.OrdinalIgnoreCase)
            || (top.Equals("Users", StringComparison.OrdinalIgnoreCase) && rest.Split('\\').Length <= 2);
    }

    private static IEnumerable<string> StoreProblems(StoreElement o)
    {
        if (o.Accounts.Count == 0)
        {
            yield return "Školský obchod nemá žiadne konto, ktoré ho smie používať.";
        }

        foreach (var a in o.Accounts)
        {
            if (!AccountPattern().IsMatch(a) || SessionPolicy.NeverClean.Contains(a))
            {
                yield return $"konto „{a}“ nemôže používať Školský obchod.";
            }
        }

        if (o.MaxPerSession is < 1 or > 10)
        {
            yield return "najviac programov v relácii musí byť 1 až 10.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in o.Items)
        {
            var label = item.Id.Length > 0 ? item.Id : "(bez mena)";
            if (!IdPattern().IsMatch(item.Id) || !ids.Add(item.Id))
            {
                yield return $"položka {label}: chýba, je neplatné alebo dvakrát použité id.";
            }

            if (string.IsNullOrWhiteSpace(item.Title))
            {
                yield return $"položka {label}: chýba názov pre žiakov.";
            }

            if (!item.Program.Present)
            {
                yield return $"položka {label}: program v obchode sa inštaluje, nemôže byť present: false.";
            }

            if (item.Program.Installer is not null)
            {
                yield return $"položka {label}: v Školskom obchode len programy z WinGet alebo balíky zo zásoby (package), nie installer.";
            }

            foreach (var p in ProgramProblems(item.Program))
            {
                yield return $"položka {label}: {p}";
            }
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

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,128}$")]
    private static partial Regex SystemNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._\- ]{1,20}$")]
    private static partial Regex AccountPattern();

    [GeneratedRegex(@"^[\p{L}0-9 \-]{1,40}$")]
    private static partial Regex SandboxNamePattern();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex WindowsPath();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^(?![0-9]+$)[A-Za-z0-9_-]{1,15}$")]
    private static partial Regex WorkgroupPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 .()+-]{0,79}$")]
    private static partial Regex TimeZonePattern();

    [GeneratedRegex(@"^[2-9][0-9]H[12]$")]
    private static partial Regex ReleasePattern();
}
