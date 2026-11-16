using Polaris.Agent;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>The templates of the Sandbox and the School store: what is written, what is checked, what is refused.</summary>
public static class StoreChecks
{
    private static readonly DateTimeOffset T = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);

    private static List<string> Problems(params Element[] e) => RecipeCheck.Problems(new Recipe { Name = "O", Elements = [.. e] });

    private static StoreItem Item(string id, string display) => new()
    {
        Id = id,
        Title = display,
        Program = new ProgramElement { Id = id, Winget = "Vydavatel." + display, DisplayName = display },
    };

    private static StoreElement Store() => new() { Id = "obchod", Accounts = ["ziak"], MaxPerSession = 2, Items = [Item("a", "Alfa"), Item("b", "Beta"), Item("c", "Gama")] };

    [Check]
    public static void TheSandboxTemplateIsStrictByDefault()
    {
        var x = SandboxConfig.Xml(new SandboxElement { Id = "s", Name = "Pokusy" });
        foreach (var line in new[] { "<Networking>Disable</Networking>", "<ClipboardRedirection>Disable</ClipboardRedirection>", "<AudioInput>Disable</AudioInput>", "<VideoInput>Disable</VideoInput>", "<ProtectedClient>Enable</ProtectedClient>", "<MemoryInMB>4096</MemoryInMB>" })
        {
            Expect.True(x.Contains(line, StringComparison.Ordinal), "predvolene: " + line);
        }

        Expect.True(!x.Contains("MappedFolders", StringComparison.Ordinal) && !x.Contains("LogonCommand", StringComparison.Ordinal), "bez priečinkov a príkazu");
        Expect.Equal(x, SandboxConfig.Xml(new SandboxElement { Id = "s", Name = "Pokusy" }), "vždy rovnaký text");

        var odd = SandboxConfig.Xml(new SandboxElement
        {
            Id = "s",
            Name = "Web",
            Networking = true,
            Folders = [new MappedFolder { HostFolder = @"C:\Učivo & <testy>" }],
            LogonCommand = "cmd /c echo \"a\" > b",
        });
        Expect.True(odd.Contains(@"<HostFolder>C:\Učivo &amp; &lt;testy&gt;</HostFolder>", StringComparison.Ordinal), "znaky XML sa zakódujú");
        Expect.True(odd.Contains("<ReadOnly>true</ReadOnly>", StringComparison.Ordinal), "priečinok len na čítanie");
        Expect.True(odd.Contains("&quot;a&quot; &gt; b", StringComparison.Ordinal), "príkaz zakódovaný: " + odd);
        Expect.Equal("Pieskovisko – Web.wsb", new SandboxElement { Name = " Web " }.FileName, "meno súboru");
    }

    [Check]
    public static void ChecksOfSandboxTemplates()
    {
        Expect.Sequence(Array.Empty<string>(), Problems(new SandboxElement { Id = "s", Name = "Pokusy 2" }), "dobrá šablóna");
        Expect.True(Problems(new SandboxElement { Id = "s", Name = "a/b" }).Count > 0, "zlé meno");
        Expect.True(Problems(new SandboxElement { Id = "s", Name = "A", MemoryMb = 512 }).Any(p => p.Contains("MB", StringComparison.Ordinal)), "málo pamäte");
        Expect.True(Problems(new SandboxElement { Id = "s", Name = "A", Folders = [new() { HostFolder = "/etc" }] }).Any(p => p.Contains("celá cesta", StringComparison.Ordinal)), "nie cesta Windows");
        Expect.True(Problems(new SandboxElement { Id = "s", Name = "A", Folders = [new() { HostFolder = @"C:\Data\..\Windows" }] }).Count > 0, "..");
        foreach (var system in new[] { @"C:\", @"C:\Windows\System32", @"C:\Program Files", @"C:\ProgramData\x", @"C:\Users\ziak" })
        {
            Expect.True(Problems(new SandboxElement { Id = "s", Name = "A", Folders = [new() { HostFolder = system, ReadOnly = false }] }).Any(p => p.Contains("systémový", StringComparison.Ordinal)), "zapisovať do " + system);
            Expect.Sequence(Array.Empty<string>(), Problems(new SandboxElement { Id = "s", Name = "A", Folders = [new() { HostFolder = system }] }), "čítať " + system);
        }

        Expect.Sequence(Array.Empty<string>(), Problems(new SandboxElement { Id = "s", Name = "A", Folders = [new() { HostFolder = @"C:\Users\Public\Documents\Vystupy", ReadOnly = false }] }), "zapisovateľný priečinok na výstupy");
        Expect.True(Problems(new SandboxElement { Id = "a", Name = "X" }, new SandboxElement { Id = "b", Name = "x" }).Any(p => p.Contains("to isté", StringComparison.Ordinal)), "dve šablóny s rovnakým menom");
    }

    [Check]
    public static void ChecksOfTheStore()
    {
        Expect.Sequence(Array.Empty<string>(), Problems(Store()), "dobrý obchod");
        Expect.True(Problems(new StoreElement { Id = "o", Items = [Item("a", "Alfa")] }).Any(p => p.Contains("žiadne konto", StringComparison.Ordinal)), "bez kont");
        Expect.True(Problems(new StoreElement { Id = "o", Accounts = ["Administrator"], Items = [Item("a", "Alfa")] }).Any(p => p.Contains("nemôže", StringComparison.Ordinal)), "správca");
        var bad = Store();
        bad.Items.Add(Item("a", "Delta"));
        Expect.True(Problems(bad).Any(p => p.Contains("dvakrát", StringComparison.Ordinal)), "dve položky s jedným id");
        var file = Store();
        file.Items[0].Program = new ProgramElement { Id = "a", DisplayName = "Alfa", Installer = new InstallerSource { File = "subory/alfa.msi", Sha256 = new string('a', 64) } };
        Expect.True(Problems(file).Any(p => p.Contains("len programy z WinGet", StringComparison.Ordinal)), "inštalátor zo zásoby zatiaľ nie");
        var gone = Store();
        gone.Items[0].Program.Present = false;
        Expect.True(Problems(gone).Any(p => p.Contains("present: false", StringComparison.Ordinal)), "odstraňovanie v obchode");
        Expect.True(Problems(Store(), new ProgramElement { Id = "alfa", Winget = "Vydavatel.Alfa", DisplayName = "Alfa" }).Count > 0, "natrvalo aj v obchode");

        var sample = RecipeText.Load(Path.Combine(Repo.Root, "recepty", "ukazka-relacia.json"));
        Expect.Sequence(Array.Empty<string>(), RecipeCheck.Problems(sample), "ukážka s obchodom");
        var store = sample.Elements.OfType<StoreElement>().Single();
        Expect.True(store.Items.All(i => i.Program.Winget is { Length: > 0 }), "programy obchodu z WinGet");
        var sandboxes = RecipeText.Load(Path.Combine(Repo.Root, "recepty", "ukazka-pieskovisko.json"));
        Expect.Equal(2, sandboxes.Elements.OfType<SandboxElement>().Count(), "dve šablóny v ukážke");
    }

    [Check]
    public static void WhatTheStoreRefuses()
    {
        var c = Store();
        StoreInstall Had(string item, string account = "ziak") => new() { Item = item, Account = account, InstalledUtc = T };
        Expect.Equal(null, StoreRules.Refusal(c, "ziak", false, [], "a", false), "žiak smie");
        Expect.Equal(null, StoreRules.Refusal(c, "ZIAK", false, [], "a", false), "veľkosť písmen konta");
        Expect.True(StoreRules.Refusal(null, "ziak", false, [], "a", false)!.Contains("zatvorený", StringComparison.Ordinal), "bez katalógu");
        Expect.True(StoreRules.Refusal(c, "ziak", true, [], "a", false)!.Contains("Správca", StringComparison.Ordinal), "správca nie");
        Expect.True(StoreRules.Refusal(c, "ucitel", false, [], "a", false)!.Contains("nemôže", StringComparison.Ordinal), "iné konto");
        Expect.True(StoreRules.Refusal(c, "ziak", false, [], "x", false)!.Contains("nie je", StringComparison.Ordinal), "neznáma položka");
        Expect.True(StoreRules.Refusal(c, "ziak", false, [], null, false)!.Contains("nie je", StringComparison.Ordinal), "bez položky");
        Expect.True(StoreRules.Refusal(c, "ziak", false, [Had("a")], "a", false)!.Contains("už nainštalovaný", StringComparison.Ordinal), "dvakrát");
        Expect.True(StoreRules.Refusal(c, "ziak", false, [], "a", true)!.Contains("natrvalo", StringComparison.Ordinal), "už je na PC z receptu");
        Expect.True(StoreRules.Refusal(c, "ziak", false, [Had("a"), Had("b")], "c", false)!.Contains("najviac 2", StringComparison.Ordinal), "limit relácie");
        Expect.Equal(null, StoreRules.Refusal(c, "ziak", false, [Had("a"), Had("b", "ziak2")], "c", false), "cudzie inštalácie sa nerátajú");
    }

    [Check]
    public static void RequestsAreSmallAndStrict()
    {
        Expect.Equal("install", StoreRules.RequestFromJson("{\"command\":\"install\",\"item\":\"a\"}")!.Command, "požiadavka");
        Expect.Equal(null, StoreRules.RequestFromJson("nie json"), "pokazená");
        Expect.Equal(null, StoreRules.RequestFromJson(null), "žiadna");
        Expect.Equal(null, StoreRules.RequestFromJson("{\"command\":\"" + new string('x', StoreRules.MaxRequest) + "\"}"), "príliš dlhá");
        var answer = new StoreAnswer { Ok = true, Items = [new StoreOffer { Id = "a", Title = "Alfa\nnový riadok" }] };
        var line = StoreRules.ToJson(answer);
        Expect.True(!line.Contains('\n', StringComparison.Ordinal), "odpoveď je jeden riadok");
        Expect.Equal("Alfa\nnový riadok", StoreRules.AnswerFromJson(line)!.Items[0].Title, "späť");
        var install = new StoreInstall { Item = "a", Account = "ziak", InstalledUtc = T, Program = Item("a", "Alfa").Program };
        Expect.Equal("Vydavatel.Alfa", StoreRules.InstallFromJson(StoreRules.ToJson(install))!.Program.Winget, "záznam inštalácie");
    }

    [Check]
    public static void ThePupilsMenu()
    {
        var asked = new List<StoreRequest>();
        var typed = new Queue<string?>(["2"]);
        var host = new AgentHost
        {
            Handlers = _ => [],
            IsAdmin = () => false,
            JournalPath = "",
            Clock = () => T,
            Confirm = _ => false,
            Trusted = () => [],
            AddTrusted = _ => { },
            RemoveTrusted = _ => false,
            ReadLine = () => typed.Count > 0 ? typed.Dequeue() : null,
            Store = r =>
            {
                asked.Add(r);
                return r.Command == "list"
                    ? new StoreAnswer { Ok = true, Items = [new() { Id = "a", Title = "Alfa", Description = "zvuk", Installed = true }, new() { Id = "b", Title = "Beta" }] }
                    : new StoreAnswer { Ok = true, Message = "Beta je nainštalovaný." };
            },
        };

        (int Code, string Text) Run(params string[] args)
        {
            var w = new StringWriter();
            return (Commands.Run(args, w, host), w.ToString());
        }

        var (code, text) = Run("obchod");
        Expect.Equal(0, code, text);
        Expect.True(text.Contains("1. Alfa – zvuk  [nainštalovaný]", StringComparison.Ordinal) && text.Contains("2. Beta", StringComparison.Ordinal), text);
        Expect.True(asked.Count == 2 && asked[1].Command == "install" && asked[1].Item == "b", "druhá položka");

        asked.Clear();
        Expect.Equal(0, Run("obchod", "zoznam").Code, "len zoznam");
        Expect.Equal(1, asked.Count, "zoznam nič neinštaluje");
        Expect.Equal(0, Run("obchod", "instalovat", "b").Code, "priamo");
        Expect.Equal(2, Run("obchod", "instalovat").Code, "bez položky");
        typed.Enqueue("9");
        Expect.Equal(2, Run("obchod").Code, "zlé číslo");
        Expect.Equal(0, Run("obchod").Code, "Enter = koniec");
    }
}
