namespace Polaris.Core;

/// <summary>One lesson of the school day by the bell: number, start and end (local time).</summary>
public sealed record Period(int Number, TimeOnly Start, TimeOnly End);

/// <summary>One cell of the timetable: on this weekday (ISO: 1 = Monday … 7 = Sunday), in this lesson, this group of
/// PCs runs this profile.</summary>
public sealed record Lesson(int Weekday, int Period, string Group, string Profile);

/// <summary>A one-time change by a teacher or the admin: on this date and lesson the group runs another profile.</summary>
public sealed record Override(DateOnly Date, int Period, string Group, string Profile);

/// <summary>A moment when a group should switch to a profile (the PCs restart into it).</summary>
public sealed record Switch(DateTime At, string Group, string Profile, string Reason);

/// <summary>The timetable of the classroom: bells, lessons, one-time changes, days off. It answers which profile a group
/// should run at a moment and when the next switches are – a switch happens in the break before a lesson, and only
/// when the profile really changes (no needless restarts).</summary>
public sealed class Timetable
{
    public List<Period> Periods { get; set; } = [];

    public List<Lesson> Lessons { get; set; } = [];

    public List<Override> Overrides { get; set; } = [];

    /// <summary>Holidays and other days without lessons: the group runs its default profile all day.</summary>
    public HashSet<DateOnly> DaysOff { get; set; } = [];

    /// <summary>How many minutes before a lesson the switch happens (the PCs need a restart).</summary>
    public int SwitchMinutesBefore { get; set; } = 3;

    /// <summary>ISO weekday: 1 = Monday … 7 = Sunday.</summary>
    public static int IsoWeekday(DateOnly date) => date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;

    public static string WeekdayName(int iso) => iso switch
    {
        1 => "pondelok",
        2 => "utorok",
        3 => "streda",
        4 => "štvrtok",
        5 => "piatok",
        6 => "sobota",
        7 => "nedeľa",
        _ => "?",
    };

    /// <summary>The profile the group should run in a given lesson of a given day (null = its default profile).</summary>
    public string? ProfileFor(DateOnly date, int period, string group)
    {
        if (DaysOff.Contains(date))
        {
            return null;
        }

        var once = Overrides.LastOrDefault(o => o.Date == date && o.Period == period && o.Group == group);
        if (once is not null)
        {
            return once.Profile;
        }

        return Lessons.FirstOrDefault(l => l.Weekday == IsoWeekday(date) && l.Period == period && l.Group == group)?.Profile;
    }

    /// <summary>The profile the group should run at a moment: the lesson running now, else the next lesson today once its
    /// switch moment has come, else the last profile of the day stays until evening; outside lessons the default.</summary>
    public string ProfileAt(DateTime moment, string group, string defaultProfile)
    {
        var date = DateOnly.FromDateTime(moment);
        var time = TimeOnly.FromDateTime(moment);
        var current = defaultProfile;
        foreach (var period in Periods.OrderBy(p => p.Start))
        {
            var switchAt = period.Start.AddMinutes(-SwitchMinutesBefore);
            if (time < switchAt)
            {
                break;
            }

            current = ProfileFor(date, period.Number, group) ?? defaultProfile;
        }

        return current;
    }

    /// <summary>The switches of one day for one group, in order. Consecutive lessons with the same profile give one
    /// switch; the day starts from the default profile.</summary>
    public List<Switch> SwitchesOf(DateOnly date, string group, string defaultProfile)
    {
        var list = new List<Switch>();
        var current = defaultProfile;
        foreach (var period in Periods.OrderBy(p => p.Start))
        {
            var wanted = ProfileFor(date, period.Number, group) ?? defaultProfile;
            if (wanted == current)
            {
                continue;
            }

            var at = date.ToDateTime(period.Start.AddMinutes(-SwitchMinutesBefore));
            var reason = Overrides.Any(o => o.Date == date && o.Period == period.Number && o.Group == group)
                ? $"{period.Number}. hodina (jednorazová zmena)"
                : $"{period.Number}. hodina";
            list.Add(new Switch(at, group, wanted, reason));
            current = wanted;
        }

        return list;
    }

    /// <summary>Problems a person should fix before the timetable is used (Slovak, one line each).</summary>
    public List<string> Problems(IReadOnlyCollection<string> knownProfiles, IReadOnlyCollection<string> knownGroups)
    {
        var problems = new List<string>();
        var ordered = Periods.OrderBy(p => p.Start).ToList();
        foreach (var p in ordered)
        {
            if (p.End <= p.Start)
            {
                problems.Add($"{p.Number}. hodina končí skôr, než začne.");
            }
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Start < ordered[i - 1].End)
            {
                problems.Add($"{ordered[i - 1].Number}. a {ordered[i].Number}. hodina sa prekrývajú.");
            }

            var gap = ordered[i].Start - ordered[i - 1].End;
            if (gap < TimeSpan.FromMinutes(SwitchMinutesBefore) && ordered[i].Start >= ordered[i - 1].End)
            {
                problems.Add($"Prestávka pred {ordered[i].Number}. hodinou je kratšia ako {SwitchMinutesBefore} min – PC by sa reštartovali počas hodiny.");
            }
        }

        foreach (var dup in Periods.GroupBy(p => p.Number).Where(g => g.Count() > 1))
        {
            problems.Add($"{dup.Key}. hodina je v zvonení viackrát.");
        }

        var numbers = Periods.Select(p => p.Number).ToHashSet();
        foreach (var l in Lessons)
        {
            var where = $"{WeekdayName(l.Weekday)}, {l.Period}. hodina, skupina {l.Group}";
            if (l.Weekday is < 1 or > 7)
            {
                problems.Add($"Neplatný deň týždňa {l.Weekday} (1 = pondelok … 7 = nedeľa).");
            }

            if (!numbers.Contains(l.Period))
            {
                problems.Add($"{where}: taká hodina v zvonení nie je.");
            }

            if (!knownProfiles.Contains(l.Profile))
            {
                problems.Add($"{where}: neznámy profil {l.Profile}.");
            }

            if (!knownGroups.Contains(l.Group))
            {
                problems.Add($"{where}: neznáma skupina {l.Group}.");
            }
        }

        foreach (var dup in Lessons.GroupBy(l => (l.Weekday, l.Period, l.Group)).Where(g => g.Select(x => x.Profile).Distinct().Count() > 1))
        {
            problems.Add($"{WeekdayName(dup.Key.Weekday)}, {dup.Key.Period}. hodina, skupina {dup.Key.Group}: viac rôznych profilov naraz.");
        }

        foreach (var o in Overrides)
        {
            if (!numbers.Contains(o.Period))
            {
                problems.Add($"Jednorazová zmena {o.Date:d. M. yyyy}: {o.Period}. hodina v zvonení nie je.");
            }

            if (!knownProfiles.Contains(o.Profile))
            {
                problems.Add($"Jednorazová zmena {o.Date:d. M. yyyy}: neznámy profil {o.Profile}.");
            }
        }

        return problems;
    }
}
