using Polaris.Core;

namespace Polaris.Tests;

public static class TimetableChecks
{
    // a made-up school day (bells), not a real school's
    private static Timetable Week()
    {
        var t = new Timetable
        {
            Periods =
            [
                new(1, new TimeOnly(8, 0), new TimeOnly(8, 45)),
                new(2, new TimeOnly(8, 55), new TimeOnly(9, 40)),
                new(3, new TimeOnly(10, 0), new TimeOnly(10, 45)),
                new(4, new TimeOnly(10, 55), new TimeOnly(11, 40)),
            ],
            Lessons =
            [
                new(1, 1, "ucebna", "KANCELARSKY"),
                new(1, 2, "ucebna", "KANCELARSKY"),
                new(1, 3, "ucebna", "GRAFICKY"),
                new(1, 4, "ucebna", "PROGRAMOVANIE"),
                new(2, 2, "ucebna", "GRAFICKY"),
            ],
        };
        return t;
    }

    private static readonly DateOnly Monday = new(2026, 11, 9);
    private static readonly DateOnly Tuesday = new(2026, 11, 10);

    [Check]
    public static void IsoWeekdays()
    {
        Expect.Equal(1, Timetable.IsoWeekday(Monday), "pondelok = 1");
        Expect.Equal(7, Timetable.IsoWeekday(new DateOnly(2026, 11, 15)), "nedeľa = 7");
        Expect.Equal("štvrtok", Timetable.WeekdayName(4), "názov dňa");
    }

    [Check]
    public static void SwitchesOnlyWhenProfileChanges()
    {
        var s = Week().SwitchesOf(Monday, "ucebna", "ZAKLADNY");
        // 1st lesson: ZAKLADNY → KANCELARSKY; 2nd the same (no switch); 3rd GRAFICKY; 4th PROGRAMOVANIE
        Expect.Sequence(["KANCELARSKY", "GRAFICKY", "PROGRAMOVANIE"], s.Select(x => x.Profile), "profily prepnutí");
        Expect.Equal(Monday.ToDateTime(new TimeOnly(7, 57)), s[0].At, "prepnutie 3 min pred 1. hodinou");
        Expect.Equal(Monday.ToDateTime(new TimeOnly(9, 57)), s[1].At, "prepnutie cez prestávku pred 3. hodinou");
    }

    [Check]
    public static void EmptyLessonFallsBackToDefault()
    {
        var s = Week().SwitchesOf(Tuesday, "ucebna", "ZAKLADNY");
        // Tuesday: only the 2nd lesson is GRAFICKY; the 3rd returns to the default
        Expect.Sequence(["GRAFICKY", "ZAKLADNY"], s.Select(x => x.Profile), "návrat k predvolenému");
    }

    [Check]
    public static void ProfileAtMoments()
    {
        var t = Week();
        Expect.Equal("ZAKLADNY", t.ProfileAt(Monday.ToDateTime(new TimeOnly(7, 30)), "ucebna", "ZAKLADNY"), "ráno pred prepnutím");
        Expect.Equal("KANCELARSKY", t.ProfileAt(Monday.ToDateTime(new TimeOnly(7, 58)), "ucebna", "ZAKLADNY"), "po prepnutí pred 1. hodinou");
        Expect.Equal("KANCELARSKY", t.ProfileAt(Monday.ToDateTime(new TimeOnly(9, 50)), "ucebna", "ZAKLADNY"), "veľká prestávka pred prepnutím");
        Expect.Equal("GRAFICKY", t.ProfileAt(Monday.ToDateTime(new TimeOnly(10, 20)), "ucebna", "ZAKLADNY"), "počas 3. hodiny");
        Expect.Equal("PROGRAMOVANIE", t.ProfileAt(Monday.ToDateTime(new TimeOnly(15, 0)), "ucebna", "ZAKLADNY"), "popoludní ostáva posledný");
    }

    [Check]
    public static void OverrideWinsForOneDay()
    {
        var t = Week();
        t.Overrides.Add(new Override(Monday, 3, "ucebna", "TEST"));
        var s = t.SwitchesOf(Monday, "ucebna", "ZAKLADNY");
        Expect.True(s.Any(x => x.Profile == "TEST" && x.Reason.Contains("jednorazová", StringComparison.Ordinal)), "jednorazová zmena v pláne");
        var nextMonday = t.SwitchesOf(Monday.AddDays(7), "ucebna", "ZAKLADNY");
        Expect.True(nextMonday.All(x => x.Profile != "TEST"), "o týždeň neplatí");
    }

    [Check]
    public static void DayOffRunsDefault()
    {
        var t = Week();
        t.DaysOff.Add(Monday);
        Expect.Equal(0, t.SwitchesOf(Monday, "ucebna", "ZAKLADNY").Count, "sviatok bez prepínania");
        Expect.Equal("ZAKLADNY", t.ProfileAt(Monday.ToDateTime(new TimeOnly(10, 20)), "ucebna", "ZAKLADNY"), "sviatok = predvolený");
    }

    [Check]
    public static void OtherGroupIsUntouched()
    {
        var s = Week().SwitchesOf(Monday, "kabinet", "ZAKLADNY");
        Expect.Equal(0, s.Count, "skupina bez rozvrhu sa neprepína");
    }

    [Check]
    public static void ProblemsAreFound()
    {
        var t = Week();
        t.Periods.Add(new Period(5, new TimeOnly(11, 41), new TimeOnly(12, 20))); // a 1-minute break
        t.Lessons.Add(new Lesson(8, 1, "ucebna", "KANCELARSKY"));
        t.Lessons.Add(new Lesson(3, 9, "ucebna", "KANCELARSKY"));
        t.Lessons.Add(new Lesson(1, 1, "ucebna", "NEZNAMY"));
        var problems = t.Problems(["KANCELARSKY", "GRAFICKY", "PROGRAMOVANIE"], ["ucebna"]);
        Expect.True(problems.Any(p => p.Contains("kratšia", StringComparison.Ordinal)), "krátka prestávka");
        Expect.True(problems.Any(p => p.Contains("Neplatný deň", StringComparison.Ordinal)), "deň 8");
        Expect.True(problems.Any(p => p.Contains("taká hodina v zvonení nie je", StringComparison.Ordinal)), "9. hodina");
        Expect.True(problems.Any(p => p.Contains("neznámy profil NEZNAMY", StringComparison.Ordinal)), "neznámy profil");
        Expect.True(problems.Any(p => p.Contains("viac rôznych profilov", StringComparison.Ordinal)), "dva profily naraz");
    }

    [Check]
    public static void CleanTimetableHasNoProblems()
    {
        var problems = Week().Problems(["KANCELARSKY", "GRAFICKY", "PROGRAMOVANIE"], ["ucebna"]);
        Expect.Equal(0, problems.Count, "bez problémov: " + string.Join("; ", problems));
    }
}
