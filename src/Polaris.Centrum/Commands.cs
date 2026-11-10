using System.Globalization;
using System.Text;
using Polaris.Core;
using Polaris.Net;

namespace Polaris.Centrum;

/// <summary>A mistake in what the person typed; shown as one Slovak line, nothing is saved.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>The commands of polaris-centrum. Every command loads the settings file; commands that change something save
/// it (safely, with a numbered backup). Nothing else is touched.</summary>
public static class Commands
{
    public const string DefaultFile = "nastavenia.json";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string Help = """
        polaris-centrum – nastavenia centra POLARIS (zvonenie, skupiny, profily, rozvrh, voľné dni)
        Pracuje len so súborom nastavení – nič neposiela po sieti a žiadne PC nemení.

          [--subor CESTA]                     súbor nastavení (inak nastavenia.json v aktuálnom priečinku)

          zvonenie                            vypíše zvonenie
          zvonenie ČÍSLO OD DO                nastaví hodinu, napr. zvonenie 1 8:00 8:45
          zvonenie zrus ČÍSLO                 zruší hodinu zo zvonenia
          profily                             vypíše profily
          profil MENO [zmrazeny|trvaly] [POPIS…]   pridá alebo upraví profil
          skupiny                             vypíše skupiny PC
          skupina MENO PROFIL                 pridá skupinu alebo jej zmení predvolený profil
          hodina DEŇ ČÍSLO SKUPINA PROFIL     zapíše hodinu do rozvrhu, napr. hodina po 1 ucebna KANCELARSKY
          hodina DEŇ ČÍSLO SKUPINA -          vymaže hodinu z rozvrhu
          zmena DÁTUM ČÍSLO SKUPINA PROFIL    jednorazová zmena (PROFIL - ju zruší)
          volno OD [DO]                       voľný deň alebo voľné dni (celý deň predvolený profil)
          volno zrus OD [DO]                  zruší voľné dni
          rozvrh [SKUPINA]                    týždenný rozvrh
          den [DÁTUM] [SKUPINA]               kedy sa PC v daný deň prepnú (inak dnes)
          kontrola                            nájde chyby v nastaveniach (návratový kód 1, keď nejaké sú)

        DEŇ: po ut st št pi so ne (alebo 1–7)   DÁTUM: dnes, zajtra, 16.11.2026, 16.11., 2026-11-16
        """;

    /// <summary>Runs one command. Returns 0 = done, 1 = the check found problems, 2 = wrong input or unreadable file.</summary>
    public static int Run(string[] args, TextWriter output, DateTime localNow, DateTimeOffset utcNow)
    {
        var words = args.ToList();
        var file = DefaultFile;
        var at = words.IndexOf("--subor");
        if (at >= 0)
        {
            if (at + 1 >= words.Count)
            {
                output.WriteLine("Chyba: po --subor chýba cesta k súboru.");
                return 2;
            }

            file = words[at + 1];
            words.RemoveRange(at, 2);
        }

        if (words.Count == 0 || words[0] is "pomoc" or "-h" or "--help" or "/?")
        {
            output.Write(Help);
            return 0;
        }

        try
        {
            var store = new CentreDataStore(file);
            var (data, note) = store.Load();
            if (note is not null)
            {
                output.WriteLine("Pozor: " + note);
            }

            var command = words[0];
            var rest = words.Skip(1).ToList();
            var today = DateOnly.FromDateTime(localNow);
            if (command == "kontrola")
            {
                return Check(data, rest, output);
            }

            var changed = command switch
            {
                "zvonenie" => Bells(data, rest, output),
                "profily" => ListProfiles(data, rest, output),
                "profil" => SetProfile(data, rest, output),
                "skupiny" => ListGroups(data, rest, output),
                "skupina" => SetGroup(data, rest, output),
                "hodina" => SetLesson(data, rest, output),
                "zmena" => SetOverride(data, rest, today, output),
                "volno" => DaysOff(data, rest, today, output),
                "rozvrh" => Week(data, rest, output),
                "den" => Day(data, rest, today, output),
                _ => throw new UsageException($"Neznámy príkaz „{command}“. Zoznam príkazov: polaris-centrum pomoc"),
            };

            if (changed)
            {
                store.Save(data, utcNow);
                output.WriteLine($"Uložené do {file}.");
            }

            return 0;
        }
        catch (UsageException e)
        {
            output.WriteLine("Chyba: " + e.Message);
            return 2;
        }
        catch (InvalidDataException e)
        {
            output.WriteLine("Chyba: " + e.Message);
            return 2;
        }
        catch (IOException e)
        {
            output.WriteLine("Chyba pri práci so súborom: " + e.Message);
            return 2;
        }
        catch (UnauthorizedAccessException e)
        {
            output.WriteLine("Chyba – prístup k súboru odmietnutý: " + e.Message);
            return 2;
        }
    }

    private static bool Bells(CentreData data, List<string> a, TextWriter o)
    {
        var periods = data.Timetable.Periods;
        if (a.Count == 0)
        {
            if (periods.Count == 0)
            {
                o.WriteLine("Zvonenie je prázdne. Pridaj hodinu: zvonenie 1 8:00 8:45");
            }

            foreach (var p in periods.OrderBy(p => p.Start))
            {
                o.WriteLine($"{p.Number,2}. hodina  {Time(p.Start),5} – {Time(p.End),5}");
            }

            return false;
        }

        if (a[0] == "zrus")
        {
            Need(a, 2, "zvonenie zrus ČÍSLO");
            var number = Number(a[1], "číslo hodiny");
            if (periods.RemoveAll(p => p.Number == number) == 0)
            {
                throw new UsageException($"{number}. hodina v zvonení nie je.");
            }

            o.WriteLine($"{number}. hodina zrušená zo zvonenia (zápisy v rozvrhu ostali – kontrola ich ukáže).");
            return true;
        }

        Need(a, 3, "zvonenie ČÍSLO OD DO");
        var n = Number(a[0], "číslo hodiny");
        var start = ParseTime(a[1]);
        var end = ParseTime(a[2]);
        if (end <= start)
        {
            throw new UsageException("Hodina musí skončiť neskôr, než začne.");
        }

        periods.RemoveAll(p => p.Number == n);
        periods.Add(new Period(n, start, end));
        periods.Sort((x, y) => x.Start.CompareTo(y.Start));
        o.WriteLine($"{n}. hodina: {Time(start)} – {Time(end)}.");
        return true;
    }

    private static bool ListProfiles(CentreData data, List<string> a, TextWriter o)
    {
        Need(a, 0, "profily");
        if (data.Profiles.Count == 0)
        {
            o.WriteLine("Žiadne profily. Pridaj: profil KANCELARSKY zmrazeny kancelárske programy");
        }

        foreach (var p in data.Profiles.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var policy = p.Policy == ProfilePolicy.Frozen ? "zmrazený" : "trvalý";
            var active = p.Active > 0 ? $"beží v{p.Active}" : "bez schválenej verzie";
            var description = p.Description.Length > 0 ? " – " + p.Description : "";
            o.WriteLine($"{p.Name} ({policy}, {active}, verzií {p.Versions.Count}){description}");
        }

        return false;
    }

    private static bool SetProfile(CentreData data, List<string> a, TextWriter o)
    {
        if (a.Count < 1)
        {
            throw new UsageException("Použitie: profil MENO [zmrazeny|trvaly] [POPIS…]");
        }

        var name = ProfileName(a[0]);
        var rest = a.Skip(1).ToList();
        ProfilePolicy? policy = null;
        if (rest.Count > 0 && rest[0] is "zmrazeny" or "zmrazený" or "trvaly" or "trvalý")
        {
            policy = rest[0].StartsWith("zmraz", StringComparison.Ordinal) ? ProfilePolicy.Frozen : ProfilePolicy.Persistent;
            rest.RemoveAt(0);
        }

        var profile = data.FindProfile(name);
        var created = profile is null;
        if (profile is null)
        {
            profile = new Profile { Name = name };
            data.Profiles.Add(profile);
        }

        if (policy is { } p)
        {
            profile.Policy = p;
        }

        if (rest.Count > 0)
        {
            profile.Description = string.Join(' ', rest);
        }

        o.WriteLine(created ? $"Profil {name} pridaný." : $"Profil {name} upravený.");
        return true;
    }

    private static bool ListGroups(CentreData data, List<string> a, TextWriter o)
    {
        Need(a, 0, "skupiny");
        if (data.Fleet.Groups.Count == 0)
        {
            o.WriteLine("Žiadne skupiny. Pridaj: skupina ucebna ZAKLADNY");
        }

        foreach (var g in data.Fleet.Groups.OrderBy(g => g.Name, StringComparer.Ordinal))
        {
            o.WriteLine($"{g.Name} – predvolený profil {g.DefaultProfile}, PC: {data.Fleet.InGroup(g.Name).Count()}");
        }

        return false;
    }

    private static bool SetGroup(CentreData data, List<string> a, TextWriter o)
    {
        Need(a, 2, "skupina MENO PROFIL");
        var profile = KnownProfile(data, a[1]);
        var group = data.Fleet.Groups.FirstOrDefault(g => g.Name == a[0]);
        if (group is null)
        {
            data.Fleet.Groups.Add(new Group { Name = a[0], DefaultProfile = profile });
            o.WriteLine($"Skupina {a[0]} pridaná, predvolený profil {profile}.");
        }
        else
        {
            group.DefaultProfile = profile;
            o.WriteLine($"Skupina {a[0]}: predvolený profil {profile}.");
        }

        return true;
    }

    private static bool SetLesson(CentreData data, List<string> a, TextWriter o)
    {
        Need(a, 4, "hodina DEŇ ČÍSLO SKUPINA PROFIL");
        var day = Weekday(a[0]);
        var period = KnownPeriod(data, a[1]);
        var group = KnownGroup(data, a[2]);
        var lessons = data.Timetable.Lessons;
        var where = $"{Timetable.WeekdayName(day)}, {period}. hodina, skupina {group}";
        lessons.RemoveAll(l => l.Weekday == day && l.Period == period && l.Group == group);
        if (a[3] == "-")
        {
            o.WriteLine($"{where}: vymazané (beží predvolený profil).");
            return true;
        }

        var profile = KnownProfile(data, a[3]);
        lessons.Add(new Lesson(day, period, group, profile));
        lessons.Sort((x, y) => (x.Weekday, x.Period).CompareTo((y.Weekday, y.Period)));
        o.WriteLine($"{where}: {profile}.");
        return true;
    }

    private static bool SetOverride(CentreData data, List<string> a, DateOnly today, TextWriter o)
    {
        Need(a, 4, "zmena DÁTUM ČÍSLO SKUPINA PROFIL");
        var date = ParseDate(a[0], today);
        var period = KnownPeriod(data, a[1]);
        var group = KnownGroup(data, a[2]);
        var list = data.Timetable.Overrides;
        list.RemoveAll(x => x.Date == date && x.Period == period && x.Group == group);
        var where = $"{Date(date)}, {period}. hodina, skupina {group}";
        if (a[3] == "-")
        {
            o.WriteLine($"{where}: jednorazová zmena zrušená.");
            return true;
        }

        var profile = KnownProfile(data, a[3]);
        list.Add(new Override(date, period, group, profile));
        list.Sort((x, y) => (x.Date, x.Period).CompareTo((y.Date, y.Period)));
        if (date < today)
        {
            o.WriteLine("Pozor: dátum je v minulosti.");
        }

        o.WriteLine($"{where}: jednorazovo {profile}.");
        return true;
    }

    private static bool DaysOff(CentreData data, List<string> a, DateOnly today, TextWriter o)
    {
        var remove = a.Count > 0 && a[0] == "zrus";
        if (remove)
        {
            a = a.Skip(1).ToList();
        }

        if (a.Count == 0)
        {
            var list = data.Timetable.DaysOff.Where(d => d >= today).Order().ToList();
            o.WriteLine(list.Count == 0 ? "Žiadne voľné dni od dnes." : "Voľné dni od dnes: " + string.Join(", ", list.Select(Date)));
            return false;
        }

        if (a.Count > 2)
        {
            throw new UsageException("Použitie: volno [zrus] OD [DO]");
        }

        var from = ParseDate(a[0], today);
        var to = a.Count == 2 ? ParseDate(a[1], today) : from;
        if (to < from)
        {
            throw new UsageException("Koniec voľna je pred začiatkom.");
        }

        if (to.DayNumber - from.DayNumber > 366)
        {
            throw new UsageException("Voľno dlhšie ako rok – asi preklep v dátume.");
        }

        var count = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            count += (remove ? data.Timetable.DaysOff.Remove(d) : data.Timetable.DaysOff.Add(d)) ? 1 : 0;
        }

        var range = from == to ? Date(from) : $"{Date(from)} – {Date(to)}";
        o.WriteLine(remove ? $"Voľno zrušené: {range} ({count} dní)." : $"Voľno: {range} ({count} nových dní).");
        return count > 0;
    }

    private static bool Week(CentreData data, List<string> a, TextWriter o)
    {
        if (a.Count > 1)
        {
            throw new UsageException("Použitie: rozvrh [SKUPINA]");
        }

        var tt = data.Timetable;
        if (tt.Periods.Count == 0)
        {
            o.WriteLine("Zvonenie je prázdne – najprv: zvonenie 1 8:00 8:45");
            return false;
        }

        foreach (var group in Groups(data, a))
        {
            var fallback = data.Fleet.Groups.First(g => g.Name == group).DefaultProfile;
            var days = Enumerable.Range(1, 5)
                .Concat(tt.Lessons.Where(l => l.Group == group && l.Weekday > 5).Select(l => l.Weekday).Distinct().Order())
                .ToList();
            var cells = tt.Periods.OrderBy(p => p.Start).Select(p => days
                .Select(d => tt.Lessons.FirstOrDefault(l => l.Weekday == d && l.Period == p.Number && l.Group == group)?.Profile ?? "·")
                .ToList()).ToList();
            var width = Math.Max(9, cells.SelectMany(r => r).Append(fallback).Max(c => c.Length) + 2);
            o.WriteLine($"Rozvrh skupiny {group} (· = predvolený profil {fallback})");
            var head = new StringBuilder(new string(' ', 17));
            foreach (var d in days)
            {
                head.Append(Timetable.WeekdayName(d).PadRight(width));
            }

            o.WriteLine(head.ToString().TrimEnd());
            var row = 0;
            foreach (var p in tt.Periods.OrderBy(p => p.Start))
            {
                var line = new StringBuilder($"{p.Number,2}. {Time(p.Start),5}–{Time(p.End),5}  ");
                foreach (var c in cells[row])
                {
                    line.Append(c.PadRight(width));
                }

                o.WriteLine(line.ToString().TrimEnd());
                row++;
            }

            o.WriteLine();
        }

        return false;
    }

    private static bool Day(CentreData data, List<string> a, DateOnly today, TextWriter o)
    {
        if (a.Count > 2)
        {
            throw new UsageException("Použitie: den [DÁTUM] [SKUPINA]");
        }

        var date = today;
        var groupArgs = a;
        if (a.Count > 0 && TryParseDate(a[0], today, out var d))
        {
            date = d;
            groupArgs = a.Skip(1).ToList();
        }

        var tt = data.Timetable;
        foreach (var group in Groups(data, groupArgs))
        {
            var fallback = data.Fleet.Groups.First(g => g.Name == group).DefaultProfile;
            o.WriteLine($"{Timetable.WeekdayName(Timetable.IsoWeekday(date))} {Date(date)} – skupina {group} (predvolený {fallback})");
            if (tt.DaysOff.Contains(date))
            {
                o.WriteLine("  voľný deň – celý deň " + fallback);
                continue;
            }

            var switches = tt.SwitchesOf(date, group, fallback);
            if (switches.Count == 0)
            {
                o.WriteLine("  bez prepnutia – celý deň " + fallback);
                continue;
            }

            foreach (var s in switches)
            {
                o.WriteLine($"  {Time(TimeOnly.FromDateTime(s.At)),5}  → {s.Profile}  ({s.Reason})");
            }

            var last = switches[^1].Profile;
            if (last != fallback)
            {
                o.WriteLine($"  {last} ostáva do konca dňa; po reštarte na druhý deň začne {fallback}.");
            }
        }

        return false;
    }

    private static int Check(CentreData data, List<string> a, TextWriter o)
    {
        Need(a, 0, "kontrola");
        var profiles = data.Profiles.Select(p => p.Name).ToHashSet();
        var groups = data.Fleet.Groups.Select(g => g.Name).ToHashSet();
        var problems = data.Timetable.Problems(profiles, groups);
        foreach (var g in data.Fleet.Groups.Where(g => !profiles.Contains(g.DefaultProfile)))
        {
            problems.Add($"Skupina {g.Name}: neznámy predvolený profil {g.DefaultProfile}.");
        }

        foreach (var o2 in data.Timetable.Overrides.Where(x => !groups.Contains(x.Group)))
        {
            problems.Add($"Jednorazová zmena {Date(o2.Date)}: neznáma skupina {o2.Group}.");
        }

        if (data.Timetable.Periods.Count == 0)
        {
            problems.Add("Zvonenie je prázdne.");
        }

        if (groups.Count == 0)
        {
            problems.Add("Nie je žiadna skupina PC.");
        }

        if (problems.Count == 0)
        {
            o.WriteLine($"V poriadku. Hodiny v zvonení: {data.Timetable.Periods.Count}, skupiny: {groups.Count}, profily: {profiles.Count}, " +
                $"zápisy v rozvrhu: {data.Timetable.Lessons.Count}.");
            return 0;
        }

        o.WriteLine($"Nájdené problémy ({problems.Count}):");
        foreach (var p in problems)
        {
            o.WriteLine("  – " + p);
        }

        return 1;
    }

    // ---- helpers ----

    private static void Need(List<string> a, int count, string usage)
    {
        if (a.Count != count)
        {
            throw new UsageException("Použitie: " + usage);
        }
    }

    private static int Number(string text, string what) =>
        int.TryParse(text, NumberStyles.None, Inv, out var n) && n is >= 0 and <= 20
            ? n
            : throw new UsageException($"Neplatné {what}: „{text}“.");

    private static string ProfileName(string text)
    {
        var name = text.ToUpperInvariant();
        if (name.Length == 0 || name.Length > 40 || name == "-" || name.Any(char.IsWhiteSpace))
        {
            throw new UsageException($"Neplatné meno profilu „{text}“.");
        }

        return name;
    }

    private static string KnownProfile(CentreData data, string text)
    {
        var name = ProfileName(text);
        return data.FindProfile(name) is not null
            ? name
            : throw new UsageException($"Profil {name} neexistuje. Pridaj ho: profil {name}");
    }

    private static string KnownGroup(CentreData data, string text) =>
        data.Fleet.Groups.Any(g => g.Name == text)
            ? text
            : throw new UsageException($"Skupina {text} neexistuje. Pridaj ju: skupina {text} PROFIL");

    private static int KnownPeriod(CentreData data, string text)
    {
        var n = Number(text, "číslo hodiny");
        return data.Timetable.Periods.Any(p => p.Number == n)
            ? n
            : throw new UsageException($"{n}. hodina nie je v zvonení. Pridaj ju: zvonenie {n} OD DO");
    }

    private static IEnumerable<string> Groups(CentreData data, List<string> a)
    {
        if (a.Count == 1)
        {
            return [KnownGroup(data, a[0])];
        }

        if (a.Count > 1)
        {
            throw new UsageException("Uveď najviac jednu skupinu.");
        }

        return data.Fleet.Groups.Count > 0
            ? data.Fleet.Groups.Select(g => g.Name).Order(StringComparer.Ordinal)
            : throw new UsageException("Nie je žiadna skupina PC. Pridaj ju: skupina ucebna PROFIL");
    }

    public static int Weekday(string text) => text.ToLowerInvariant() switch
    {
        "1" or "po" or "pondelok" => 1,
        "2" or "ut" or "utorok" => 2,
        "3" or "st" or "streda" => 3,
        "4" or "št" or "stv" or "štvrtok" or "stvrtok" => 4,
        "5" or "pi" or "piatok" => 5,
        "6" or "so" or "sobota" => 6,
        "7" or "ne" or "nedeľa" or "nedela" => 7,
        _ => throw new UsageException($"Neznámy deň „{text}“ (po ut st št pi so ne alebo 1–7)."),
    };

    public static TimeOnly ParseTime(string text) =>
        TimeOnly.TryParseExact(text, ["H:mm", "H.mm"], Inv, DateTimeStyles.None, out var t)
            ? t
            : throw new UsageException($"Neplatný čas „{text}“ (napr. 8:00).");

    public static DateOnly ParseDate(string text, DateOnly today) =>
        TryParseDate(text, today, out var d) ? d : throw new UsageException($"Neplatný dátum „{text}“ (napr. 16.11.2026).");

    public static bool TryParseDate(string text, DateOnly today, out DateOnly date)
    {
        switch (text)
        {
            case "dnes":
                date = today;
                return true;
            case "zajtra":
                date = today.AddDays(1);
                return true;
        }

        if (DateOnly.TryParseExact(text, ["d.M.yyyy", "yyyy-MM-dd"], Inv, DateTimeStyles.None, out date))
        {
            return true;
        }

        var parts = text.Split('.');
        if (parts.Length == 3 && parts[2].Length == 0
            && int.TryParse(parts[0], NumberStyles.None, Inv, out var day)
            && int.TryParse(parts[1], NumberStyles.None, Inv, out var month)
            && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(today.Year, month))
        {
            date = new DateOnly(today.Year, month, day);
            return true;
        }

        date = default;
        return false;
    }

    private static string Time(TimeOnly t) => t.ToString("H:mm", Inv);

    private static string Date(DateOnly d) => d.ToString("d. M. yyyy", Inv);
}
