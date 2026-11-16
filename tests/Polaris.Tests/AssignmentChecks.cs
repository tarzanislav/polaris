using Polaris.Core;
using Polaris.Recipes;

namespace Polaris.Tests;

public static class AssignmentChecks
{
    // Wednesday 18 November 2026
    private static readonly DateTime Wednesday = new(2026, 11, 18, 9, 0, 0);
    private static readonly DateTimeOffset Issued = new(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);
    private static readonly PcTarget Pc7 = new("pc-07", ["ucebna"], "relacia-1");

    private static RegistryValueElement Value(string data, bool present = true) =>
        new() { Id = "v", Path = @"HKLM\SOFTWARE\Skuska", Name = "A", Kind = RegistryKind.DWord, Data = data, Present = present };

    private static Assignment For(Scope scope, Element element, int minutesLater = 0, LifetimeKind lifetime = LifetimeKind.Permanent) => new()
    {
        Element = element,
        Scope = scope,
        Lifetime = lifetime,
        Start = Wednesday.Date,
        IssuedUtc = Issued.AddMinutes(minutesLater),
        IssuedBy = "Správca",
    };

    private static string? Winner(IEnumerable<Assignment> list, DateTime? at = null, Timetable? timetable = null) =>
        Assignments.Effective(list, Pc7, at ?? Wednesday, timetable) is [var only] ? (only.Element.Present ? ((RegistryValueElement)only.Element).Data : "odstránené") : null;

    [Check]
    public static void NarrowerScopeWins()
    {
        Assignment[] list =
        [
            For(Scope.School, Value("1")),
            For(Scope.Group("ucebna"), Value("2")),
            For(Scope.Pc("pc-07"), Value("3")),
        ];
        Expect.Equal("3", Winner(list), "PC pred skupinou a školou");
        Expect.Equal("2", Winner(list[..2]), "skupina pred školou");
        Expect.Equal("odstránené", Winner([.. list, For(Scope.Session("relacia-1"), Value("0", present: false))]), "relácia odstráni");
        Expect.Equal("3", Winner([.. list, For(Scope.Session("ina-relacia"), Value("9"))]), "cudzia relácia neplatí");
        Expect.Equal("1", Winner([list[0], For(Scope.Group("ina-skupina"), Value("8")), For(Scope.Pc("pc-08"), Value("9"))]), "iná skupina a iné PC neplatia");
    }

    [Check]
    public static void OfTwoEquallyNarrowTheLaterWins()
    {
        Expect.Equal("5", Winner([For(Scope.School, Value("5"), minutesLater: 10), For(Scope.School, Value("4"))]), "neskôr vydané");
    }

    [Check]
    public static void LifetimesEndAtTheRightMoment()
    {
        var range = For(Scope.Pc("pc-07"), Value("1"), lifetime: LifetimeKind.Range);
        range.Start = Wednesday;
        range.End = Wednesday.AddHours(2);
        Expect.True(!Assignments.IsActive(range, Wednesday.AddMinutes(-1)), "pred začiatkom");
        Expect.True(Assignments.IsActive(range, Wednesday.AddMinutes(119)), "v rozsahu");
        Expect.True(!Assignments.IsActive(range, Wednesday.AddHours(2)), "po konci");
        range.End = null;
        Expect.True(!Assignments.IsActive(range, Wednesday), "rozsah bez konca neplatí");

        var day = For(Scope.Pc("pc-07"), Value("1"), lifetime: LifetimeKind.UntilEndOfDay);
        Expect.True(Assignments.IsActive(day, Wednesday.Date.AddHours(23).AddMinutes(59)), "do polnoci");
        Expect.True(!Assignments.IsActive(day, Wednesday.Date.AddDays(1)), "o polnoci koniec");

        var week = For(Scope.Pc("pc-07"), Value("1"), lifetime: LifetimeKind.UntilEndOfWeek);
        Expect.True(Assignments.IsActive(week, new DateTime(2026, 11, 22, 23, 59, 0)), "nedeľa večer");
        Expect.True(!Assignments.IsActive(week, new DateTime(2026, 11, 23, 0, 0, 0)), "pondelok o polnoci koniec");
        week.Start = new DateTime(2026, 11, 22, 10, 0, 0);
        Expect.True(!Assignments.IsActive(week, new DateTime(2026, 11, 23, 0, 0, 0)), "zadané v nedeľu platí do konca nedele");
    }

    [Check]
    public static void LessonFollowsTheTimetable()
    {
        var timetable = new Timetable { Periods = [new Period(1, new TimeOnly(8, 0), new TimeOnly(8, 45)), new Period(2, new TimeOnly(8, 55), new TimeOnly(9, 40))] };
        var lesson = For(Scope.Group("ucebna"), Value("7"), lifetime: LifetimeKind.Lesson);
        lesson.Period = 2;
        var at = (int h, int m) => Wednesday.Date.AddHours(h).AddMinutes(m);
        Expect.True(!Assignments.IsActive(lesson, at(8, 51), timetable), "pred prepnutím");
        Expect.True(Assignments.IsActive(lesson, at(8, 52), timetable), "3 minúty pred hodinou");
        Expect.True(Assignments.IsActive(lesson, at(9, 39), timetable), "počas hodiny");
        Expect.True(!Assignments.IsActive(lesson, at(9, 40), timetable), "po zvonení");
        Expect.True(!Assignments.IsActive(lesson, at(9, 0).AddDays(1), timetable), "na druhý deň nie");
        Expect.True(!Assignments.IsActive(lesson, at(9, 0)), "bez rozvrhu nie");
        Expect.Equal("7", Winner([For(Scope.School, Value("1")), lesson], at(9, 0), timetable), "počas hodiny platí hodina");
        Expect.Equal("1", Winner([For(Scope.School, Value("1")), lesson], at(10, 0), timetable), "po hodine sa vráti škola");
    }

    [Check]
    public static void PartsOfBookmarksAreDecidedOneByOne()
    {
        static BookmarksElement Marks(string folder, params Browser[] browsers) => new()
        {
            Id = "zal",
            Folder = folder,
            ShowBar = false,
            Browsers = [.. browsers],
            Items = [new Bookmark { Name = "x", Url = "https://a.sk" }],
        };

        var effective = Assignments.Effective(
            [For(Scope.Group("ucebna"), Marks("Skupina", Browser.Edge, Browser.Chrome)), For(Scope.Pc("pc-07"), Marks("PC", Browser.Edge))],
            Pc7,
            Wednesday);
        string FolderOf(string path) => ((RegistryValueElement)effective.Single(e => ((RegistryValueElement)e.Element).Path == path).Element).Data;
        Expect.Equal(2, effective.Count, "dva prehliadače");
        Expect.True(FolderOf(Expansion.EdgePolicies).Contains("\"PC\"", StringComparison.Ordinal), "Edge z PC");
        Expect.True(FolderOf(Expansion.ChromePolicies).Contains("Skupina", StringComparison.Ordinal), "Chrome zo skupiny");
        Expect.Equal(ScopeKind.Group, effective[0].From.Scope.Kind, "najprv širší rozsah");
    }

    [Check]
    public static void RecipeBecomesPermanentAssignments()
    {
        var recipe = RecipeText.Load(Repo.SampleRecipe);
        var list = Assignments.FromRecipe(recipe, Scope.Group("ucebna"), Wednesday.Date, Issued, "Správca");
        Expect.Equal(recipe.Elements.Count, list.Count, "jedno priradenie na prvok");
        Expect.True(list.All(a => a.Lifetime == LifetimeKind.Permanent && a.Scope == Scope.Group("ucebna")), "natrvalo pre skupinu");
        var simple = Assignments.Effective(list, Pc7, Wednesday).Count;
        Expect.Equal(Expansion.ExpandAll(recipe.Elements).Count(), simple, "všetky jednoduché prvky platia");
    }
}
