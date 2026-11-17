using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>Basic settings of a PC (K4a): bytes in the registry, the workgroup and time zone, Windows Update and power by
/// policies. What they are made of is checked here on any system; the real Windows parts in WindowsAgentChecks.</summary>
public static class SettingsChecks
{
    private const string Recipe = """
        {
          "schema": 1,
          "name": "Základ učebne",
          "elements": [
            { "type": "computer", "id": "pc", "workgroup": "SKOLA", "timeZone": "Central Europe Standard Time" },
            { "type": "updates", "id": "aktualizacie", "activeHoursStart": 7, "activeHoursEnd": 17, "deferFeatureDays": 60, "targetRelease": "25H2" },
            { "type": "power", "id": "napajanie", "sleepMinutes": 0, "displayMinutes": 15, "powerButton": "ShutDown", "fastStartup": false },
            { "type": "user-registry", "id": "efekty", "path": "Control Panel\\Desktop", "name": "UserPreferencesMask", "kind": "Binary", "data": "90 12 03 80 10 00 00 00" }
          ]
        }
        """;

    [Check]
    public static void BinaryDataIsHexadecimalPairs()
    {
        Expect.Sequence(new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0, 0, 0 }, (byte[])RegistryData.Parse(RegistryKind.Binary, "90 12 03 80, 10 00 00 00")!, "s medzerami a čiarkou");
        Expect.Sequence(new byte[] { 0xAB, 0xCD }, (byte[])RegistryData.Parse(RegistryKind.Binary, "abCD")!, "malé aj veľké písmená");
        Expect.Equal(0, ((byte[])RegistryData.Parse(RegistryKind.Binary, "")!).Length, "prázdna hodnota je dovolená");
        foreach (var bad in new[] { "9", "9 0", "GG", "0x90", new string('0', (RegistryData.MaxBinary * 2) + 2) })
        {
            Expect.True(RegistryData.Parse(RegistryKind.Binary, bad) is null, $"„{bad[..Math.Min(bad.Length, 10)]}“ nie sú bajty");
        }

        Expect.Equal("9012", RegistryData.Show(new byte[] { 0x90, 0x12 }), "zobrazenie");
        var element = new RegistryValueElement { Id = "b", Path = @"HKLM\SOFTWARE\Skuska", Name = "B", Kind = RegistryKind.Binary, Data = "XY" };
        Expect.True(RecipeCheck.ElementProblems(element).Any(p => p.Contains("Binary", StringComparison.Ordinal)), "kontrola receptu povie, že to nie sú bajty");
    }

    [Check]
    public static void SettingsReadWriteAndKeepTheirForm()
    {
        var recipe = RecipeText.FromJson(Recipe);
        Expect.Equal(0, RecipeCheck.Problems(recipe).Count, "ukážka je v poriadku: " + string.Join(" ", RecipeCheck.Problems(recipe)));
        var computer = (ComputerElement)recipe.Elements[0];
        Expect.Equal("SKOLA", computer.Workgroup, "pracovná skupina");
        var power = (PowerElement)recipe.Elements[2];
        Expect.Equal(PowerAction.ShutDown, power.PowerButton, "tlačidlo napájania");
        Expect.Equal(false, power.FastStartup, "rýchle spustenie");
        Expect.True(power.LidClose is null && power.WakePassword is null, "nevyplnené ostáva nevyplnené");

        var again = RecipeText.FromJson(RecipeText.ToJson(recipe));
        Expect.Equal(RecipeText.Canonical(recipe), RecipeText.Canonical(again), "zapísaný a znova prečítaný je ten istý");
        Expect.True(!RecipeText.Canonical(recipe).Contains("lidClose", StringComparison.Ordinal), "nevyplnené polia sa nepíšu");

        Expect.Throws<InvalidDataException>(() => RecipeText.FromJson("""{ "schema": 1, "elements": [ { "type": "power", "id": "p", "sleepMinute": 5 } ] }"""), "preklep v poli neprejde");
        Expect.Throws<InvalidDataException>(() => RecipeText.FromJson("""{ "schema": 1, "elements": [ { "type": "power", "id": "p", "powerButton": "Explode" } ] }"""), "neznáma činnosť tlačidla");
    }

    [Check]
    public static void UpdatesBecomePolicyValues()
    {
        var updates = (UpdatesElement)RecipeText.FromJson(Recipe).Elements[1];
        var values = Values(updates);
        Expect.Equal("1", values["SetActiveHours"], "aktívne hodiny zapnuté");
        Expect.Equal("7", values["ActiveHoursStart"], "od 7");
        Expect.Equal("17", values["ActiveHoursEnd"], "do 17");
        Expect.Equal("60", values["DeferFeatureUpdatesPeriodInDays"], "odklad verzie");
        Expect.Equal("25H2", values["TargetReleaseVersionInfo"], "verzia Windows");
        Expect.Equal("Windows 11", values["ProductVersion"], "produkt");
        Expect.True(!values.ContainsKey("DeferQualityUpdates") && !values.ContainsKey("ExcludeWUDriversInQualityUpdate"), "nevyplnené sa nenastaví");

        var all = SettingsExpansion.Updates(updates).Cast<RegistryValueElement>().ToList();
        Expect.True(all.All(v => v.Path == SettingsExpansion.UpdatePolicies), "všetko v politikách Windows Update");
        Expect.True(all.Where(v => !v.Present).Select(v => v.Name).Contains("DeferQualityUpdatesPeriodInDays"), "nevyplnená politika sa odstráni");

        updates.Present = false;
        Expect.True(SettingsExpansion.Updates(updates).All(v => !v.Present), "odstránený prvok odstráni všetky politiky");
    }

    [Check]
    public static void UpdatesAreChecked()
    {
        Expect.True(Problems(new UpdatesElement { Id = "u", ActiveHoursStart = 7 }).Any(p => p.Contains("oba konce", StringComparison.Ordinal)), "len začiatok");
        Expect.Equal(0, Problems(new UpdatesElement { Id = "u", ActiveHoursStart = 22, ActiveHoursEnd = 6 }).Count, "cez polnoc 8 hodín je v poriadku");
        Expect.True(Problems(new UpdatesElement { Id = "u", ActiveHoursStart = 6, ActiveHoursEnd = 2 }).Any(p => p.Contains("18", StringComparison.Ordinal)), "20 hodín je viac, ako Windows dovolí");
        Expect.True(Problems(new UpdatesElement { Id = "u", ActiveHoursStart = 8, ActiveHoursEnd = 8 }).Count > 0, "nulové aktívne hodiny");
        Expect.True(Problems(new UpdatesElement { Id = "u", DeferQualityDays = 31 }).Count > 0, "mesačné najviac 30 dní");
        Expect.True(Problems(new UpdatesElement { Id = "u", TargetRelease = "Windows 11" }).Count > 0, "verzia v tvare 25H2");
    }

    [Check]
    public static void PowerBecomesPolicyValuesForSocketAndBattery()
    {
        var power = (PowerElement)RecipeText.FromJson(Recipe).Elements[2];
        var all = SettingsExpansion.Power(power).Cast<RegistryValueElement>().ToList();
        string Of(string guid, string name) => all.Single(v => v.Path.EndsWith(guid, StringComparison.Ordinal) && v.Name == name).Data;

        Expect.Equal("0", Of(SettingsExpansion.SleepAfter, "ACSettingIndex"), "spánok nikdy v sieti");
        Expect.Equal("0", Of(SettingsExpansion.SleepAfter, "DCSettingIndex"), "aj na batérii");
        Expect.Equal("900", Of(SettingsExpansion.DisplayAfter, "ACSettingIndex"), "obrazovka po 15 minútach = 900 sekúnd");
        Expect.Equal("3", Of(SettingsExpansion.PowerButtonAction, "DCSettingIndex"), "tlačidlo vypne PC");
        Expect.True(all.Where(v => v.Path.EndsWith(SettingsExpansion.LidCloseAction, StringComparison.Ordinal)).All(v => !v.Present), "veko nevyplnené = politika odstránená");
        var fast = all.Single(v => v.Name == "HiberbootEnabled");
        Expect.True(fast.Present && fast.Data == "0" && fast.Path == SettingsExpansion.FastStartupKey, "rýchle spustenie vypnuté");

        power.Present = false;
        var removed = SettingsExpansion.Power(power).ToList();
        Expect.True(removed.All(v => !v.Present), "odstránený prvok odstráni politiky");
        Expect.True(removed.Cast<RegistryValueElement>().All(v => v.Name != "HiberbootEnabled"), "hodnotu Windows pre rýchle spustenie nikdy neodstraňuje");

        Expect.True(Problems(new PowerElement { Id = "p" }).Count > 0, "prázdny prvok nič nenastavuje");
        Expect.True(Problems(new PowerElement { Id = "p", SleepMinutes = -1 }).Count > 0, "záporný čas");
        Expect.True(Problems(new PowerElement { Id = "p", DisplayMinutes = SettingsExpansion.MaxMinutes + 1 }).Count > 0, "viac ako týždeň");
    }

    [Check]
    public static void ComputerIsChecked()
    {
        Expect.Equal(0, Problems(new ComputerElement { Id = "c", Workgroup = "SKOLA" }).Count, "SKOLA");
        Expect.Equal(0, Problems(new ComputerElement { Id = "c", TimeZone = "W. Europe Standard Time" }).Count, "pásmo s bodkou");
        Expect.True(Problems(new ComputerElement { Id = "c", Workgroup = "123" }).Count > 0, "len číslice");
        Expect.True(Problems(new ComputerElement { Id = "c", Workgroup = "SKOLA S MEDZEROU" }).Count > 0, "medzera a priveľa znakov");
        Expect.True(Problems(new ComputerElement { Id = "c", TimeZone = "..\\Run" }).Count > 0, "zlé meno pásma");
        Expect.True(Problems(new ComputerElement { Id = "c" }).Count > 0, "nič nenastavuje");
        Expect.True(Problems(new ComputerElement { Id = "c", Workgroup = "SKOLA", Present = false }).Count > 0, "nedá sa odstrániť");
    }

    [Check]
    public static void TwoElementsCannotSetTheSamePolicy()
    {
        var recipe = new Recipe
        {
            Name = "x",
            Elements =
            [
                new PowerElement { Id = "a", SleepMinutes = 0 },
                new RegistryValueElement { Id = "b", Path = SettingsExpansion.PowerPolicies + "\\" + SettingsExpansion.SleepAfter, Name = "ACSettingIndex", Kind = RegistryKind.DWord, Data = "60" },
            ],
        };
        Expect.True(RecipeCheck.Problems(recipe).Any(p => p.Contains("menia to isté", StringComparison.Ordinal)), "napájanie a ručná hodnota sa bijú");
    }

    [Check]
    public static void TheSampleOfBasicSettingsIsValid()
    {
        var sample = RecipeText.Load(Path.Combine(Repo.Root, "recepty", "ukazka-zaklad.json"));
        Expect.Equal(0, RecipeCheck.Problems(sample).Count, "ukazka-zaklad.json: " + string.Join(" ", RecipeCheck.Problems(sample)));
        Expect.Equal("SKOLA", sample.Elements.OfType<ComputerElement>().Single().Workgroup, "pracovná skupina ako Samba na serveri");
    }

    private static Dictionary<string, string> Values(Element element) => Expansion.Expand(element)
        .Cast<RegistryValueElement>()
        .Where(v => v.Present)
        .ToDictionary(v => v.Name, v => v.Data);

    private static List<string> Problems(Element element) => RecipeCheck.ElementProblems(element).ToList();
}
