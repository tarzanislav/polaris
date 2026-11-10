using Polaris.Centrum;
using Polaris.Net;

namespace Polaris.Tests;

public static class CentrumChecks
{
    // Tuesday 10 November 2026, 7:30
    private static readonly DateTime Now = new(2026, 11, 10, 7, 30, 0);
    private static readonly DateTimeOffset UtcNow = new(2026, 11, 10, 6, 30, 0, TimeSpan.Zero);

    private sealed class Session : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "polaris-centrum-" + Guid.NewGuid().ToString("N"));

        public Session() => Directory.CreateDirectory(dir);

        public string File => Path.Combine(dir, "nastavenia.json");

        public (int Code, string Text) Run(string line)
        {
            var writer = new StringWriter();
            var args = new[] { "--subor", File }.Concat(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            var code = Commands.Run(args, writer, Now, UtcNow);
            return (code, writer.ToString());
        }

        public void Ok(string line)
        {
            var (code, text) = Run(line);
            Expect.True(code == 0, $"„{line}“ skončilo kódom {code}: {text}");
        }

        public CentreData Data => new CentreDataStore(File).Load().Data;

        public void Dispose() => Directory.Delete(dir, recursive: true);
    }

    private static Session Classroom()
    {
        var s = new Session();
        s.Ok("zvonenie 1 8:00 8:45");
        s.Ok("zvonenie 2 8:55 9:40");
        s.Ok("zvonenie 3 10:00 10:45");
        s.Ok("profil zakladny zmrazeny mimo vyučovania");
        s.Ok("profil kancelarsky");
        s.Ok("profil graficky");
        s.Ok("skupina ucebna ZAKLADNY");
        s.Ok("hodina ut 1 ucebna KANCELARSKY");
        s.Ok("hodina ut 2 ucebna KANCELARSKY");
        s.Ok("hodina ut 3 ucebna graficky");
        return s;
    }

    [Check]
    public static void ASchoolDayIsBuiltAndSaved()
    {
        using var s = Classroom();
        var data = s.Data;
        Expect.Equal(3, data.Timetable.Periods.Count, "zvonenie");
        Expect.Equal(3, data.Profiles.Count, "profily");
        Expect.Equal("mimo vyučovania", data.FindProfile("ZAKLADNY")!.Description, "popis profilu");
        Expect.Equal("GRAFICKY", data.Timetable.Lessons.Single(l => l.Period == 3).Profile, "meno profilu veľkými");
        Expect.True(new CentreDataStore(s.File).Backups().Any(), "pri ukladaní vznikajú zálohy");
    }

    [Check]
    public static void DayShowsOnlyRealSwitches()
    {
        using var s = Classroom();
        var (code, text) = s.Run("den");
        Expect.Equal(0, code, "kód");
        Expect.True(text.Contains("7:57  → KANCELARSKY", StringComparison.Ordinal), "prepnutie pred 1. hodinou");
        Expect.True(!text.Contains("8:52", StringComparison.Ordinal), "2. hodina má rovnaký profil – bez reštartu");
        Expect.True(text.Contains("9:57  → GRAFICKY", StringComparison.Ordinal), "prepnutie pred 3. hodinou");
    }

    [Check]
    public static void OverrideAndDayOffChangeTheDay()
    {
        using var s = Classroom();
        s.Ok("zmena dnes 3 ucebna KANCELARSKY");
        var (_, text) = s.Run("den dnes ucebna");
        Expect.True(!text.Contains("GRAFICKY", StringComparison.Ordinal), "jednorazová zmena prebije rozvrh");
        s.Ok("volno 10.11.2026");
        (_, text) = s.Run("den 10.11.2026");
        Expect.True(text.Contains("voľný deň", StringComparison.Ordinal), "voľný deň");
        s.Ok("volno 23.12.2026 7.1.2027");
        Expect.Equal(17, s.Data.Timetable.DaysOff.Count, "voľno cez Vianoce + 1 deň");
        s.Ok("volno zrus 23.12.2026 7.1.2027");
        Expect.Equal(1, s.Data.Timetable.DaysOff.Count, "zrušené voľno");
    }

    [Check]
    public static void WeekGridShowsProfiles()
    {
        using var s = Classroom();
        var (_, text) = s.Run("rozvrh");
        var lines = text.Split('\n');
        Expect.True(lines.Any(l => l.Contains("utorok", StringComparison.Ordinal) && l.Contains("piatok", StringComparison.Ordinal)), "hlavička dní");
        Expect.True(lines.Any(l => l.TrimStart().StartsWith("3.", StringComparison.Ordinal) && l.Contains("GRAFICKY", StringComparison.Ordinal)), "riadok 3. hodiny");
    }

    [Check]
    public static void MistakesAreRefusedAndNothingIsSaved()
    {
        using var s = Classroom();
        var before = System.IO.File.ReadAllText(s.File);
        Expect.Equal(2, s.Run("hodina ut 9 ucebna GRAFICKY").Code, "hodina mimo zvonenia");
        Expect.Equal(2, s.Run("hodina xx 1 ucebna GRAFICKY").Code, "neznámy deň");
        Expect.Equal(2, s.Run("hodina ut 1 ucebna NEEXISTUJE").Code, "neznámy profil");
        Expect.Equal(2, s.Run("hodina ut 1 trieda GRAFICKY").Code, "neznáma skupina");
        Expect.Equal(2, s.Run("zvonenie 4 9:00 8:00").Code, "koniec pred začiatkom");
        Expect.Equal(2, s.Run("volno 31.2.2026").Code, "neplatný dátum");
        Expect.Equal(2, s.Run("niecojine").Code, "neznámy príkaz");
        Expect.Equal(before, System.IO.File.ReadAllText(s.File), "súbor bez zmeny");
    }

    [Check]
    public static void CheckFindsAShortBreak()
    {
        using var s = Classroom();
        Expect.Equal(0, s.Run("kontrola").Code, "čistý rozvrh");
        s.Ok("zvonenie 4 10:46 11:30");
        var (code, text) = s.Run("kontrola");
        Expect.Equal(1, code, "problém nájdený");
        Expect.True(text.Contains("Prestávka pred 4. hodinou", StringComparison.Ordinal), "krátka prestávka");
    }

    [Check]
    public static void RemovingALessonFallsBackToDefault()
    {
        using var s = Classroom();
        s.Ok("hodina ut 3 ucebna -");
        Expect.Equal(2, s.Data.Timetable.Lessons.Count, "vymazaná hodina");
        var (_, text) = s.Run("den");
        Expect.True(text.Contains("→ ZAKLADNY", StringComparison.Ordinal), "po 2. hodine späť na predvolený");
    }

    [Check]
    public static void DatesAndDaysAreRead()
    {
        var today = new DateOnly(2026, 11, 10);
        Expect.Equal(new DateOnly(2026, 11, 11), Commands.ParseDate("zajtra", today), "zajtra");
        Expect.Equal(new DateOnly(2026, 11, 16), Commands.ParseDate("16.11.", today), "bez roka");
        Expect.Equal(new DateOnly(2026, 11, 16), Commands.ParseDate("2026-11-16", today), "ISO");
        Expect.Equal(4, Commands.Weekday("št"), "štvrtok");
        Expect.Equal(4, Commands.Weekday("stvrtok"), "štvrtok bez diakritiky");
        Expect.Equal(3, Commands.Weekday("st"), "streda");
        Expect.Equal(new TimeOnly(8, 5), Commands.ParseTime("8.05"), "čas s bodkou");
    }
}
