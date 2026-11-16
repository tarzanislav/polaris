using Polaris.Recipes;

namespace Polaris.Tests;

internal static class Repo
{
    /// <summary>The folder of the repository (where Polaris.slnx is), found from the running tests.</summary>
    public static string Root { get; } = Find();

    public static string SampleRecipe => Path.Combine(Root, "recepty", "ukazka-kancelarsky.json");

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Polaris.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repozitár sa nenašiel.");
    }
}

public static class RecipeChecks
{
    private static Recipe Minimal(params Element[] elements) => new() { Name = "SKUSKA", Version = 1, Elements = [.. elements] };

    private static RegistryValueElement Reg(string id, string path = @"HKLM\SOFTWARE\Skuska", string name = "A", RegistryKind kind = RegistryKind.DWord, string data = "1") =>
        new() { Id = id, Path = path, Name = name, Kind = kind, Data = data };

    private static ProgramElement Prog(string id, string display = "Skúšobný program", string? winget = "Skuska.Program") =>
        new() { Id = id, DisplayName = display, Winget = winget };

    [Check]
    public static void SampleRecipeInRepositoryIsValid()
    {
        var recipe = RecipeText.Load(Repo.SampleRecipe);
        Expect.Equal("KANCELARSKY", recipe.Name, "meno");
        Expect.Sequence(Array.Empty<string>(), RecipeCheck.Problems(recipe), "problémy ukážky");
        Expect.Equal(6, recipe.Elements.Count, "prvky");
        var marks = (BookmarksElement)recipe.Elements[^1];
        Expect.Equal("Programovanie", marks.Items[2].Name, "priečinok záložiek");
        Expect.Equal(NameMatch.Prefix, ((ProgramElement)recipe.Elements[0]).Match, "spôsob rozpoznania");
    }

    [Check]
    public static void EverythingSurvivesWritingAndReading()
    {
        var recipe = RecipeText.Load(Repo.SampleRecipe);
        var back = RecipeText.FromJson(RecipeText.ToJson(recipe));
        Expect.Equal(RecipeText.Fingerprint(recipe), RecipeText.Fingerprint(back), "odtlačok po zápise a čítaní");
        Expect.Equal("1", ((RegistryValueElement)back.Elements[3]).Data, "údaje registra");
    }

    [Check]
    public static void FingerprintIgnoresFormattingButNotContent()
    {
        const string a = """{"schema":1,"name":"P","version":2,"elements":[{"type":"registry","id":"x","path":"HKLM\\SOFTWARE\\A","name":"B","kind":"DWord","data":"1"}]}""";
        const string b = """
            {
              // komentár
              "schema": 1, "name": "P", "version": 2,
              "elements": [ { "type": "registry", "id": "x", "path": "HKLM\\SOFTWARE\\A", "name": "B", "kind": "DWord", "data": "1" }, ]
            }
            """;
        var fa = RecipeText.Fingerprint(RecipeText.FromJson(a));
        Expect.Equal(fa, RecipeText.Fingerprint(RecipeText.FromJson(b)), "iný zápis, rovnaký obsah");
        Expect.Equal(64, fa.Length, "SHA-256");
        var changed = RecipeText.FromJson(a.Replace("\"data\":\"1\"", "\"data\":\"2\"", StringComparison.Ordinal));
        Expect.True(fa != RecipeText.Fingerprint(changed), "iný obsah, iný odtlačok");
    }

    [Check]
    public static void BadTextIsExplainedInSlovak()
    {
        Expect.Throws<InvalidDataException>(() => RecipeText.FromJson("""{"schema": 9, "name": "P"}"""), "novšia schéma");
        try
        {
            RecipeText.FromJson("{\n  \"schema\": 1,\n  \"name\": \n}");
            throw new CheckFailed("pokazený text prešiel");
        }
        catch (InvalidDataException e)
        {
            Expect.True(e.Message.Contains("riadok 4", StringComparison.Ordinal), "hlási riadok: " + e.Message);
        }

        try
        {
            RecipeText.FromJson("""{"schema":1,"name":"P","elements":[{"type":"tlaciaren","id":"x"}]}""");
            throw new CheckFailed("neznámy typ prešiel");
        }
        catch (InvalidDataException e)
        {
            Expect.True(e.Message.Contains("typ", StringComparison.Ordinal), "vysvetlí typ: " + e.Message);
        }
    }

    [Check]
    public static void ChecksFindProblemsOfElements()
    {
        Expect.True(RecipeCheck.Problems(new Recipe()).Any(p => p.Contains("meno profilu", StringComparison.Ordinal)), "bez mena");

        static void Bad(Element e, string expected)
        {
            var problems = RecipeCheck.Problems(Minimal(e));
            Expect.True(problems.Any(p => p.Contains(expected, StringComparison.Ordinal)), $"čakal som „{expected}“, je: {string.Join(" / ", problems)}");
        }

        Bad(Reg("Velke-Pismena"), "malé písmená");
        Bad(Prog("p", winget: null), "práve jeden zdroj");
        Bad(new ProgramElement { Id = "p", DisplayName = "X", Winget = "A.B", Installer = new InstallerSource { File = "a.msi", Sha256 = new string('a', 64) } }, "práve jeden zdroj");
        Bad(Prog("p", winget: "bezbodky"), "platný balík WinGet");
        Bad(Prog("p", display: ""), "displayName");
        Bad(new ProgramElement { Id = "p", DisplayName = "Ab", Match = NameMatch.Prefix, Winget = "A.B" }, "aspoň 3 znaky");
        Bad(new ProgramElement { Id = "p", DisplayName = "X", Installer = new InstallerSource { File = "../mimo.msi", Sha256 = new string('a', 64) } }, "vnútri priečinka");
        Bad(new ProgramElement { Id = "p", DisplayName = "X", Installer = new InstallerSource { File = "a.msi", Sha256 = "123" } }, "SHA-256");
        Bad(Reg("r", path: @"HKCU\Software\A"), "HKLM");
        Bad(Reg("r", path: @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip"), "chránená");
        Bad(Reg("r", path: @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"), "chránená");
        Bad(Reg("r", path: @"HKLM\SOFTWARE\POLARIS\Agent"), "chránená");
        Bad(Reg("r", data: "-1"), "DWord");
        Bad(Reg("r", data: "0x1FFFFFFFF"), "DWord");
        Bad(new BookmarksElement { Id = "b", Folder = "Š", Items = [new Bookmark { Name = "x", Url = "javascript:alert(1)" }] }, "http");
        Bad(new BookmarksElement { Id = "b", Items = [new Bookmark { Name = "x", Url = "https://a.sk", Children = [] }] }, "priečinok aj záložka");
        Bad(new BookmarksElement { Id = "b", Browsers = [], Items = [new Bookmark { Name = "x", Url = "https://a.sk" }] }, "prehliadač");

        // removing needs no source and no data
        Expect.Sequence(Array.Empty<string>(), RecipeCheck.Problems(Minimal(new ProgramElement { Id = "p", DisplayName = "Starý program", Present = false })), "odstránenie bez zdroja");
        var removed = Reg("r", data: "nezmysel");
        removed.Present = false;
        Expect.Sequence(Array.Empty<string>(), RecipeCheck.Problems(Minimal(removed)), "odstránenie hodnoty bez údajov");
    }

    [Check]
    public static void ServicesAndFeaturesAreCheckedToo()
    {
        static List<string> P(Element e) => RecipeCheck.Problems(new Recipe { Name = "S", Elements = [e] });

        Expect.Sequence(Array.Empty<string>(), P(new ServiceElement { Id = "s", Name = "Spooler", Start = ServiceStart.Automatic, Running = true }), "tlač");
        Expect.True(P(new ServiceElement { Id = "s", Name = "WinDefend", Start = ServiceStart.Disabled }).Any(p => p.Contains("chránená", StringComparison.Ordinal)), "Defender sa nevypne");
        Expect.True(P(new ServiceElement { Id = "s", Name = "wuauserv", Running = false }).Any(p => p.Contains("chránená", StringComparison.Ordinal)), "aktualizácie sa nezastavia");
        Expect.Sequence(Array.Empty<string>(), P(new ServiceElement { Id = "s", Name = "WinDefend", Start = ServiceStart.Automatic }), "chránenú službu zapnúť smie");
        Expect.True(P(new ServiceElement { Id = "s", Name = "Spooler", Start = ServiceStart.Disabled, Running = true }).Any(p => p.Contains("nemôže bežať", StringComparison.Ordinal)), "vypnutá a beží");
        Expect.True(P(new ServiceElement { Id = "s", Name = "Spooler", Present = false }).Any(p => p.Contains("Disabled", StringComparison.Ordinal)), "služba sa neodstraňuje");
        Expect.True(P(new ServiceElement { Id = "s", Name = "zlé meno" }).Any(p => p.Contains("meno služby", StringComparison.Ordinal)), "meno služby");
        Expect.Sequence(Array.Empty<string>(), P(new FeatureElement { Id = "f", Name = "Containers-DisposableClientVM" }), "Sandbox");
        Expect.True(P(new FeatureElement { Id = "f", Name = "" }).Any(p => p.Contains("súčasti Windows", StringComparison.Ordinal)), "bez mena súčasti");

        var sample = RecipeText.Load(Path.Combine(Repo.Root, "recepty", "ukazka-pieskovisko.json"));
        Expect.Sequence(Array.Empty<string>(), RecipeCheck.Problems(sample), "ukážka Pieskoviska");
        var service = (ServiceElement)RecipeText.FromJson(RecipeText.ToJson(sample)).Elements[1];
        Expect.True(service.Start == ServiceStart.Automatic && service.Running == true, "služba po zápise a čítaní");
    }

    [Check]
    public static void VersionsAreComparedAsWindowsWritesThem()
    {
        Expect.True(ProgramMatch.VersionMatches("25.8.3", "25.8.3.2"), "viac častí vo Windows");
        Expect.True(ProgramMatch.VersionMatches("3.0.21", "v3.0.21"), "písmeno v");
        Expect.True(!ProgramMatch.VersionMatches("25.8", "25.80.1"), "25.8 nie je 25.80");
        Expect.True(!ProgramMatch.VersionMatches("140.0", "139.0.4"), "iná verzia");
        Expect.True(ProgramMatch.VersionMatches(null, "1.0") && ProgramMatch.VersionMatches("1.0", null), "bez verzie sa nesúdi");
    }

    [Check]
    public static void TwoElementsMustNotManageTheSameThing()
    {
        var p1 = RecipeCheck.Problems(Minimal(Reg("a"), Reg("b", path: @"hklm\software\skuska\")));
        Expect.True(p1.Any(p => p.Contains("Prvky a a b menia to isté", StringComparison.Ordinal)), "rovnaká hodnota: " + string.Join(" / ", p1));
        Expect.True(RecipeCheck.Problems(Minimal(Reg("a"), Reg("a", name: "B"))).Any(p => p.Contains("dvakrát", StringComparison.Ordinal)), "dvakrát rovnaké meno");

        var marks = new BookmarksElement { Id = "z", Browsers = [Browser.Edge], Items = [new Bookmark { Name = "x", Url = "https://a.sk" }] };
        var policy = Reg("politika", path: Expansion.EdgePolicies, name: "ManagedBookmarks", kind: RegistryKind.String, data: "[]");
        Expect.True(RecipeCheck.Problems(Minimal(marks, policy)).Any(p => p.Contains("menia to isté", StringComparison.Ordinal)), "záložky aj hodnota registra");
        Expect.True(RecipeCheck.Problems(Minimal(Prog("a"), Prog("b", winget: "Iny.Balik"))).Count == 1, "dva programy s rovnakým menom");
    }

    [Check]
    public static void BookmarksBecomePolicyValues()
    {
        var b = new BookmarksElement
        {
            Id = "zal",
            Folder = "Škola",
            Items =
            [
                new Bookmark { Name = "Wiki", Url = "https://sk.wikipedia.org/" },
                new Bookmark { Name = "Prog", Children = [new Bookmark { Name = "Py", Url = "https://docs.python.org/3/" }] },
            ],
        };
        var parts = Expansion.Expand(b).Cast<RegistryValueElement>().ToList();
        Expect.Sequence(["zal.chrome", "zal.chrome.lista", "zal.edge", "zal.edge.lista"], parts.Select(p => p.Id).Order(StringComparer.Ordinal), "rozložené prvky");
        var edge = parts.Single(p => p.Id == "zal.edge");
        Expect.Equal(Expansion.EdgePolicies, edge.Path, "kľúč Edge");
        Expect.Equal("ManagedBookmarks", edge.Name, "politika");
        Expect.Equal(
            """[{"toplevel_name":"\u0160kola"},{"name":"Wiki","url":"https://sk.wikipedia.org/"},{"name":"Prog","children":[{"name":"Py","url":"https://docs.python.org/3/"}]}]""",
            edge.Data,
            "JSON záložiek");
        Expect.Equal("BookmarkBarEnabled", parts.Single(p => p.Id == "zal.chrome.lista").Name, "lišta Chrome");

        b.Present = false;
        b.ShowBar = false;
        Expect.True(Expansion.Expand(b).All(e => !e.Present) && Expansion.Expand(b).Count() == 2, "odstránenie záložiek");
    }

    [Check]
    public static void RegistryPathsAndData()
    {
        Expect.Equal(@"HKLM\SOFTWARE\A\B", RegistryPath.Normalize(@"HKEY_LOCAL_MACHINE\\SOFTWARE\A\B\"), "jeden zápis");
        Expect.Equal(@"SOFTWARE\A", RegistryPath.UnderLocalMachine(@"hklm/SOFTWARE/A"), "časť pod HKLM");
        Expect.True(RegistryPath.UnderLocalMachine(@"HKCU\A") is null, "HKCU nie je HKLM");
        Expect.Equal((object)255u, RegistryData.Parse(RegistryKind.DWord, "0xFF"), "šestnástkovo");
        Expect.Equal((object)4294967295u, RegistryData.Parse(RegistryKind.DWord, "4294967295"), "najväčší DWord");
        Expect.Sequence(["a", "b"], (string[])RegistryData.Parse(RegistryKind.MultiString, "a\r\nb")!, "viac riadkov");
    }

    [Check]
    public static void ProgramsAreRecognisedAndUninstallCommandsSplit()
    {
        var exact = new ProgramElement { DisplayName = "VLC media player" };
        Expect.True(ProgramMatch.Matches(exact, " vlc media player "), "presné meno bez ohľadu na veľkosť");
        Expect.True(!ProgramMatch.Matches(exact, "VLC media player 2"), "presné meno nie je začiatok");
        var prefix = new ProgramElement { DisplayName = "7-Zip", Match = NameMatch.Prefix };
        Expect.True(ProgramMatch.Matches(prefix, "7-Zip 25.01 (x64)"), "začiatok mena");

        Expect.Equal(((string, string)?)(@"C:\Program Files\X\uninst.exe", "/S"), ProgramMatch.SplitCommand("\"C:\\Program Files\\X\\uninst.exe\" /S"), "v úvodzovkách");
        Expect.Equal(((string, string)?)(@"C:\X\un.EXE", "/quiet /norestart"), ProgramMatch.SplitCommand(@"C:\X\un.EXE /quiet /norestart"), "bez úvodzoviek");
        Expect.True(ProgramMatch.SplitCommand("nič") is null && ProgramMatch.SplitCommand(null) is null, "bez programu");
        Expect.Equal("{12345678-1234-1234-1234-1234567890AB}", ProgramMatch.MsiProductCode("{12345678-1234-1234-1234-1234567890AB}", null), "kód z mena kľúča");
        Expect.Equal("{12345678-1234-1234-1234-1234567890AB}", ProgramMatch.MsiProductCode("VLC", "MsiExec.exe /I{12345678-1234-1234-1234-1234567890AB}"), "kód z príkazu");
        Expect.True(ProgramMatch.MsiProductCode("VLC", @"C:\x\uninstall.exe") is null, "bez kódu");
    }
}
