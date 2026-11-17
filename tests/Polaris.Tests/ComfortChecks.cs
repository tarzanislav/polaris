using Polaris.Agent;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>Comfort of the lab, part two (K6a): the network at full power, programs starting by themselves only from a
/// list, maintenance without sleep, measuring the sign-in. The rules here, on any system; Windows in WindowsAgentChecks.</summary>
public static class ComfortChecks
{
    private const string Recipe = """
        {
          "schema": 1,
          "name": "Pohodlie",
          "elements": [
            { "type": "power", "id": "napajanie", "sleepMinutes": 0, "networkFullPower": true },
            { "type": "startup", "id": "spustanie", "allow": ["DWAgent*", "RtkAudUService"] }
          ]
        }
        """;

    [Check]
    public static void NetworkAtFullPowerIsTwoPoliciesForSocketAndBattery()
    {
        var power = (PowerElement)RecipeText.FromJson(Recipe).Elements[0];
        Expect.True(power.NetworkFullPower, "prečítané");
        var all = SettingsExpansion.Power(power).Cast<RegistryValueElement>().ToList();
        foreach (var guid in new[] { SettingsExpansion.WirelessPowerSaving, SettingsExpansion.PciExpressLinkSaving })
        {
            var values = all.Where(v => v.Path.EndsWith(guid, StringComparison.Ordinal)).ToList();
            Expect.Equal(2, values.Count, guid + ": v sieti aj na batérii");
            Expect.True(values.All(v => v.Present && v.Data == "0" && v.Path.StartsWith(SettingsExpansion.PowerPolicies, StringComparison.Ordinal)), guid + ": 0 = bez úspory, ako politika");
        }

        power.NetworkFullPower = false;
        Expect.True(SettingsExpansion.Power(power).Cast<RegistryValueElement>()
            .Where(v => v.Path.EndsWith(SettingsExpansion.WirelessPowerSaving, StringComparison.Ordinal)).All(v => !v.Present), "bez poľa rozhoduje Windows (politika odstránená)");
        Expect.True(!RecipeText.ToJson(RecipeText.FromJson(Recipe.Replace(", \"networkFullPower\": true", "", StringComparison.Ordinal))).Contains("networkFullPower", StringComparison.Ordinal),
            "predvolená hodnota sa nezapisuje – odtlačky starších receptov ostávajú");
    }

    [Check]
    public static void StartupElementReadsWritesAndIsChecked()
    {
        var recipe = RecipeText.FromJson(Recipe);
        var startup = (StartupElement)recipe.Elements[1];
        Expect.Sequence(["DWAgent*", "RtkAudUService"], startup.Allow, "zoznam povolených");
        var again = RecipeText.FromJson(RecipeText.ToJson(recipe));
        Expect.Equal(RecipeText.Fingerprint(recipe), RecipeText.Fingerprint(again), "zápis a čítanie zachová recept");
        Expect.Equal(0, RecipeCheck.Problems(recipe).Count, "v poriadku: " + string.Join(" ", RecipeCheck.Problems(recipe)));

        static List<string> Problems(params string[] allow) => [.. RecipeCheck.ElementProblems(new StartupElement { Id = "s", Allow = [.. allow] })];
        Expect.Equal(0, Problems().Count, "prázdny zoznam = vypnúť všetko okrem Zabezpečenia Windows");
        Expect.True(Problems("*").Count > 0, "„*“ samo by povolilo všetko");
        Expect.True(Problems("ab*").Count > 0, "príliš krátky začiatok");
        Expect.True(Problems("Dw*Agent").Count > 0, "hviezdička len na konci");
        Expect.True(Problems(@"C:\Program Files\x.exe").Count > 0, "bez priečinka");
        Expect.True(Problems(" ").Count > 0, "prázdne meno");
        Expect.True(Problems([.. Enumerable.Range(0, StartupRules.MaxAllow + 1).Select(i => "Program" + i)]).Count > 0, "priveľa mien");
    }

    [Check]
    public static void StartupMatchesTheNameOrTheProgram()
    {
        List<string> allow = ["DWAgent*", "RtkAudUService"];
        Expect.True(StartupRules.Allowed(allow, "DWAgentMon", ""), "začiatok mena položky");
        Expect.True(StartupRules.Allowed(allow, "Zvuk", "\"C:\\Windows\\System32\\RtkAudUService64.exe\" -background") == false, "iný program nie");
        Expect.True(StartupRules.Allowed(allow, "Zvuk", @"C:\Windows\System32\RtkAudUService.exe -background"), "meno programu bez .exe");
        Expect.True(StartupRules.Allowed([], "SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe"), "ikona Zabezpečenia Windows vždy");
        Expect.True(!StartupRules.Allowed([], "OneDrive", @"""C:\Program Files\Microsoft OneDrive\OneDrive.exe"" /background"), "prázdny zoznam nepovolí nič iné");
        Expect.True(StartupRules.Allowed(["onedrive"], "Iné meno", @"""C:\Program Files\Microsoft OneDrive\OneDrive.exe"" /background"), "veľkosť písmen nehrá rolu");

        Expect.Equal("OneDrive", StartupRules.ProgramName(@"""C:\Program Files\Microsoft OneDrive\OneDrive.exe"" /background"), "v úvodzovkách");
        Expect.Equal("SecurityHealthSystray", StartupRules.ProgramName(@"%windir%\system32\SecurityHealthSystray.exe"), "bez úvodzoviek");
        Expect.Equal("Program", StartupRules.ProgramName(@"C:\Program Files\Firma\Program.exe /tichý štart"), "medzera v priečinku bez úvodzoviek");
        Expect.Equal("skript.cmd", StartupRules.ProgramName(@"C:\Nástroje\skript.cmd"), "iný súbor ostane s príponou");
        Expect.Equal("", StartupRules.ProgramName(""), "prázdny príkaz");
    }

    [Check]
    public static void StartupMarkIsTheOneOfTaskManager()
    {
        Expect.True(StartupRules.IsOn(null), "bez značky sa spúšťa");
        Expect.True(StartupRules.IsOn([2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), "2 = zapnuté");
        Expect.True(StartupRules.IsOn([6, 0, 0, 0]), "6 = zapnuté");
        Expect.True(!StartupRules.IsOn([3, 0, 0, 0]), "3 = vypnuté");
        Expect.True(!StartupRules.IsOn([7, 0, 0, 0]), "7 = vypnuté");
        var at = new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);
        var off = StartupRules.OffValue(at);
        Expect.Equal(12, off.Length, "12 bajtov ako Správca úloh");
        Expect.True(!StartupRules.IsOn(off), "vypnuté");
        Expect.Equal(at.ToFileTime(), BitConverter.ToInt64(off, 4), "čas vypnutia");
    }

    [Check]
    public static void MaintenanceLengths()
    {
        Expect.Equal(TimeSpan.FromHours(3), Maintenance.ParseLength("3h"), "hodiny");
        Expect.Equal(TimeSpan.FromMinutes(90), Maintenance.ParseLength("90m"), "minúty");
        Expect.Equal(TimeSpan.FromMinutes(90), Maintenance.ParseLength("1h30m"), "spolu");
        Expect.Equal(TimeSpan.FromHours(24), Maintenance.ParseLength("24H"), "najviac deň");
        foreach (var bad in new[] { "", "3", "h", "0h", "25h", "1441m", "3 h", "-1h", "3d", "99999m", "1h30" })
        {
            Expect.True(Maintenance.ParseLength(bad) is null, $"„{bad}“ nie je dĺžka údržby");
        }
    }

    [Check]
    public static void MaintenanceHoldsOneRequestWhileItLasts()
    {
        var asked = new List<string>();
        var released = 0;
        var hold = new MaintenanceHold(reason =>
        {
            asked.Add(reason);
            return new Release(() => released++);
        });
        var now = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var until = now.AddHours(3);

        Expect.True(hold.Update(null, now) is null && !hold.Holding, "bez údržby nič");
        Expect.True(hold.Update(until, now) is { } began && began.Contains("nezaspí", StringComparison.Ordinal), "začala – do denníka");
        Expect.True(hold.Update(until, now.AddMinutes(1)) is null, "ďalší pohľad nič nemení");
        Expect.Equal(1, asked.Count, "jedna požiadavka");
        Expect.True(hold.Update(until.AddHours(1), now.AddMinutes(2)) is not null, "predĺžená");
        Expect.Equal(2, asked.Count, "nová požiadavka s novým časom …");
        Expect.Equal(1, released, "… stará pustená");
        Expect.True(hold.Update(until.AddHours(1), until.AddHours(1)) is { } ended && ended.Contains("skončila", StringComparison.Ordinal), "čas vypršal");
        Expect.True(!hold.Holding && released == 2, "PC môže zaspať");

        var refused = new MaintenanceHold(_ => null);
        Expect.True(refused.Update(until, now) is null && !refused.Holding, "odmietnutá požiadavka – nič do denníka …");
        Expect.True(refused.Update(until, now.AddMinutes(1)) is null && refused.Update(null, now.AddMinutes(2)) is null, "… skúša sa znova, koniec bez zápisu");
    }

    [Check]
    public static void MeasurementIsOneRecordOfTheJournal()
    {
        var group = Measurement.Group([("msedge", 100L << 20), ("explorer", 120L << 20), ("msedge", 60L << 20), ("MsEdge", 10L << 20)]);
        Expect.Equal("msedge", group[0].Name, "najväčší prvý");
        Expect.Equal(3, group[0].Count, "procesy jedného programu spolu");
        Expect.Equal(170L << 20, group[0].Bytes, "ich pamäť spolu");

        var at = new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);
        var run = Measurement.Record("ziak", 23.4, (8UL << 30, 5UL << 30), group, 1536L << 20, at, at.AddMinutes(2));
        Expect.Equal(Measurement.Recipe, run.Recipe, "druh záznamu");
        Expect.Equal(0, run.Failed, "meranie nie je chyba");
        var text = string.Join(" | ", run.Results.Select(r => r.Title + ": " + r.Message));
        Expect.True(text.Contains("pracovná plocha za 23 s", StringComparison.Ordinal), "čas prihlásenia: " + text);
        Expect.True(text.Contains("obsadené 3,0 GB z 8,0 GB (37 %)", StringComparison.Ordinal), "pamäť PC: " + text);
        Expect.True(text.Contains("msedge ×3: 170 MB", StringComparison.Ordinal), "program s počtom procesov: " + text);
        Expect.True(text.Contains("1,5 GB", StringComparison.Ordinal), "služby: " + text);
        var line = RunReport.ReadJournal(RunReport.ToLine(run) + "\n").Single();
        Expect.Equal(run.Results.Count, line.Results.Count, "riadok denníka sa prečíta späť");

        var gone = Measurement.Record("ziak", 40, null, [], 0, at, at);
        Expect.True(gone.Results.Any(r => r.Message == "relácia už skončila"), "relácia skončila pred meraním");
        Expect.Equal("512 MB", Measurement.Size(512L << 20), "MB");
    }

    [Check]
    public static void TheSampleKeepsRemoteWorkPossible()
    {
        var sample = RecipeText.Load(Path.Combine(Repo.Root, "recepty", "ukazka-zaklad.json"));
        Expect.True(sample.Elements.OfType<PowerElement>().Single().NetworkFullPower, "sieť naplno");
        var startup = sample.Elements.OfType<StartupElement>().Single();
        Expect.True(StartupRules.Allowed(startup.Allow, "DWAgent", @"""C:\Program Files\DWAgent\runtime\dwagent.exe"""), "vzdialená správa DWAgent sa spúšťa");
        Expect.True(!StartupRules.Allowed(startup.Allow, "OneDrive", @"""C:\Program Files\Microsoft OneDrive\OneDrive.exe"" /background"), "OneDrive nie");
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
