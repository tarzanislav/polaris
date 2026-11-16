using Polaris.Agent;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>A made-up PC: things by their key, with switches for a broken or a stubborn executor.</summary>
internal sealed class FakePc : IElementHandler, IReadsInBatches
{
    /// <summary>Open readings now, and how many observations happened inside one.</summary>
    public int Readings { get; private set; }

    public int ObservedInReading { get; private set; }

    public IDisposable ReadOnce()
    {
        Readings++;
        return new Done(() => Readings--);
    }

    private sealed class Done(Action done) : IDisposable
    {
        public void Dispose() => done();
    }

    public Dictionary<string, string> Things { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Broken { get; } = [];

    public HashSet<string> Stubborn { get; } = [];

    public HashSet<string> Unreadable { get; } = [];

    public List<string> Log { get; } = [];

    public bool CanHandle(Element element) => element is ProgramElement or RegistryValueElement;

    public Observation Observe(Element element)
    {
        ObservedInReading += Readings > 0 ? 1 : 0;
        if (Unreadable.Contains(element.Id))
        {
            throw new InvalidOperationException("register je zamknutý");
        }

        if (!Things.TryGetValue(element.Key, out var data))
        {
            return new Observation(Observed.Absent);
        }

        return element is RegistryValueElement r && element.Present && data != r.Data
            ? new Observation(Observed.Different, $"je {data}, má byť {r.Data}")
            : new Observation(Observed.Present);
    }

    public void Apply(Element element)
    {
        Log.Add((element.Present ? "+" : "-") + element.Id);
        if (Broken.Contains(element.Id))
        {
            throw new InvalidOperationException("inštalátor spadol");
        }

        if (Stubborn.Contains(element.Id))
        {
            return;
        }

        if (element.Present)
        {
            Things[element.Key] = element is RegistryValueElement r ? r.Data : "";
        }
        else
        {
            Things.Remove(element.Key);
        }
    }
}

public static class EngineChecks
{
    private static readonly DateTimeOffset T = new(2026, 11, 16, 18, 0, 0, TimeSpan.Zero);

    private static RegistryValueElement Reg(string id, string name, string data, bool present = true) =>
        new() { Id = id, Path = @"HKLM\SOFTWARE\Skuska", Name = name, Kind = RegistryKind.DWord, Data = data, Present = present };

    private static ProgramElement Prog(string id, string name, bool present = true) =>
        new() { Id = id, DisplayName = name, Winget = "Skuska." + id, Present = present };

    [Check]
    public static void PlanSaysWhatWouldChangeAndChangesNothing()
    {
        var pc = new FakePc();
        var same = Reg("rovnaka", "A", "1");
        var other = Reg("ina", "B", "1");
        var old = Prog("stary", "Starý program", present: false);
        pc.Things[same.Key] = "1";
        pc.Things[other.Key] = "0";
        pc.Things[old.Key] = "";
        var marks = new BookmarksElement { Id = "zal", Folder = "Š", Items = [new Bookmark { Name = "x", Url = "https://a.sk" }] };
        var plan = new Engine([pc]).Plan([same, other, Prog("novy", "Nový program"), old, marks]);

        string KindOf(string id) => plan.Items.Single(i => i.Element.Id == id).Kind.ToString();
        Expect.Equal("None", KindOf("rovnaka"), "rovnaká hodnota");
        Expect.Equal("Change", KindOf("ina"), "iná hodnota");
        Expect.Equal("Install", KindOf("novy"), "chýbajúci program");
        Expect.Equal("Remove", KindOf("stary"), "program na odstránenie");
        Expect.Equal("Install", KindOf("zal.edge"), "záložky sa rozložia na hodnoty");
        Expect.True(!plan.InDesiredState && pc.Log.Count == 0, "plán nič nezmenil");
        Expect.True(pc.ObservedInReading == plan.Items.Count && pc.Readings == 0, "plán číta PC raz pre všetky prvky a čítanie uzavrie");
        var text = RunReport.PlanText(plan, null);
        Expect.True(text.Contains("~ zmeniť: hodnota registra", StringComparison.Ordinal) && text.Contains("je 0, má byť 1", StringComparison.Ordinal), "text plánu: " + text);
    }

    [Check]
    public static void RemovalsGoFirstAndEveryChangeIsChecked()
    {
        var pc = new FakePc();
        var a = Prog("a", "Program A", present: false);
        var b = Prog("b", "Program B", present: false);
        pc.Things[a.Key] = "";
        pc.Things[b.Key] = "";
        var engine = new Engine([pc]);
        var plan = engine.Plan([Reg("r1", "X", "1"), a, Reg("r2", "Y", "2"), b]);
        var run = engine.Apply(plan, null, () => T);
        Expect.Sequence(["-b", "-a", "+r1", "+r2"], pc.Log, "poradie: odstránenie odzadu, potom pridanie");
        Expect.True(run.Failed == 0 && run.Results.All(r => r.Ok), "všetko overené");
        Expect.True(engine.Plan([Reg("r1", "X", "1"), a, Reg("r2", "Y", "2"), b]).InDesiredState, "po behu je PC podľa receptu");
    }

    [Check]
    public static void FailureOfOneNeverStopsTheOthers()
    {
        var pc = new FakePc();
        pc.Broken.Add("zly");
        pc.Stubborn.Add("tvrdohlavy");
        pc.Unreadable.Add("zamknuty");
        var engine = new Engine([pc]);
        var plan = engine.Plan([Prog("zly", "Zlý"), Reg("tvrdohlavy", "T", "1"), Reg("zamknuty", "Z", "1"), Reg("dobry", "D", "1")]);
        Expect.Equal("Cannot", plan.Items.Single(i => i.Element.Id == "zamknuty").Kind.ToString(), "nečitateľný stav");
        var run = engine.Apply(plan, null, () => T);
        Expect.True(run.Results.Single(r => r.ElementId == "dobry").Ok, "dobrý prvok prešiel");
        Expect.True(run.Results.Single(r => r.ElementId == "zly").Message.Contains("spadol", StringComparison.Ordinal), "chyba inštalátora");
        Expect.True(run.Results.Single(r => r.ElementId == "tvrdohlavy").Message.Contains("neprejavila", StringComparison.Ordinal), "overenie odhalí, že zmena nenastala");
        Expect.Equal(3, run.Failed, "dve chyby a jeden nespracovaný");
        Expect.True(RunReport.RunText(run).Contains("Hotovo s chybami", StringComparison.Ordinal), "súhrn");
    }

    [Check]
    public static void UnknownElementIsReportedNotGuessed()
    {
        var plan = new Engine([new FakePc()]).Plan([new BookmarksElement { Id = "z", Items = [new Bookmark { Name = "x", Url = "https://a.sk" }] }]);
        Expect.True(plan.Items.All(i => i.Kind == ChangeKind.Install), "záložky FakePc zvládne cez register");
        var nobody = new Engine([]).Plan([Reg("r", "A", "1")]);
        Expect.True(nobody.Items.Single().Kind == ChangeKind.Cannot && nobody.Items.Single().Before.Contains("nevie", StringComparison.Ordinal), "bez vykonávača");
    }

    [Check]
    public static void RunRecordIsAJournalLine()
    {
        var recipe = RecipeText.Load(Repo.SampleRecipe);
        var pc = new FakePc();
        var engine = new Engine([pc]);
        var run = engine.Apply(engine.Plan(recipe.Elements), recipe, () => T);
        var back = RunReport.ReadJournal(RunReport.ToLine(run) + "\n{\"pokazen");
        Expect.Equal(1, back.Count, "pokazený riadok sa preskočí");
        Expect.Equal(RecipeText.Fingerprint(recipe), back[0].Fingerprint, "odtlačok v denníku");
        Expect.Equal(run.Results.Count, back[0].Results.Count, "výsledky v denníku");
        Expect.Equal("KANCELARSKY", back[0].Recipe, "recept v denníku");
    }

    [Check]
    public static void AgentCommandsOnAMadeUpPc()
    {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var recipeFile = Path.Combine(dir, "recept.json");
            File.Copy(Repo.SampleRecipe, recipeFile);
            var pc = new FakePc();
            var admin = true;
            var answer = false;
            var host = new AgentHost
            {
                Handlers = _ => [pc],
                IsAdmin = () => admin,
                JournalPath = Path.Combine(dir, "dennik.jsonl"),
                Clock = () => T,
                Confirm = _ => answer,
                Trusted = () => [],
                AddTrusted = _ => { },
                RemoveTrusted = _ => false,
            };

            (int Code, string Text) Run(params string[] args)
            {
                var w = new StringWriter();
                var code = Commands.Run(args, w, host);
                return (code, w.ToString());
            }

            Expect.Equal(0, Run("overit", recipeFile).Code, "overit");
            Expect.Equal(RecipeText.Fingerprint(RecipeText.Load(recipeFile)), Run("odtlacok", recipeFile).Text.Trim(), "odtlacok");
            Expect.Equal(1, Run("kontrola", recipeFile).Code, "kontrola nájde rozdiely");
            Expect.Equal(2, Run("pouzi", recipeFile).Code, "bez súhlasu sa nič nerobí");
            Expect.True(pc.Log.Count == 0, "nič sa nezmenilo");
            admin = false;
            Expect.Equal(2, Run("pouzi", recipeFile, "--ano").Code, "len správca");
            admin = true;
            var applied = Run("pouzi", recipeFile, "--ano");
            Expect.Equal(0, applied.Code, "použitie: " + applied.Text);
            Expect.Equal(0, Run("kontrola", recipeFile).Code, "po použití je PC podľa receptu");
            Expect.True(Run("dennik").Text.Contains("KANCELARSKY v1", StringComparison.Ordinal), "denník");

            File.WriteAllText(recipeFile, File.ReadAllText(recipeFile).Replace("\"7zip\"", "\"vlc\"", StringComparison.Ordinal));
            var bad = Run("overit", recipeFile);
            Expect.True(bad.Code == 1 && bad.Text.Contains("dvakrát", StringComparison.Ordinal), "dvakrát rovnaké meno: " + bad.Text);
            Expect.Equal(2, Run("kontrola", recipeFile).Code, "recept s problémami sa nepoužije");
            Expect.Equal(2, Run("neznamy").Code, "neznámy príkaz");
            Expect.Equal(2, Run("overit", Path.Combine(dir, "nie-je.json")).Code, "chýbajúci súbor");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
