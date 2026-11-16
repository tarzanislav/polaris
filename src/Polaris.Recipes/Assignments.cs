using Polaris.Core;

namespace Polaris.Recipes;

/// <summary>Who an assignment is for. The order is from the widest to the narrowest; the narrower wins.</summary>
public enum ScopeKind
{
    School = 0,
    Group = 1,
    Pc = 2,
    Session = 3,
}

/// <summary>The whole school, one group (by its name), one PC (by its identifier) or one session on a PC.</summary>
public sealed record Scope(ScopeKind Kind, string Target = "")
{
    public static Scope School { get; } = new(ScopeKind.School);

    public static Scope Group(string name) => new(ScopeKind.Group, name);

    public static Scope Pc(string id) => new(ScopeKind.Pc, id);

    public static Scope Session(string id) => new(ScopeKind.Session, id);

    public override string ToString() => Kind switch
    {
        ScopeKind.School => "celá škola",
        ScopeKind.Group => "skupina " + Target,
        ScopeKind.Pc => "PC " + Target,
        _ => "relácia " + Target,
    };
}

public enum LifetimeKind
{
    /// <summary>Until somebody changes it.</summary>
    Permanent,

    /// <summary>From the start until <see cref="Assignment.End"/>.</summary>
    Range,

    /// <summary>Until midnight of the day it started.</summary>
    UntilEndOfDay,

    /// <summary>Until midnight from Sunday to Monday of the week it started.</summary>
    UntilEndOfWeek,

    /// <summary>One lesson of the timetable on the day of the start (from the switch before it until it ends).</summary>
    Lesson,
}

/// <summary>An element for somebody for some time: present or removed. An assignment is never deleted, only its state
/// changes – so the history stays and going back is one click.</summary>
public sealed class Assignment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Element Element { get; set; } = null!;

    public Scope Scope { get; set; } = Scope.School;

    public LifetimeKind Lifetime { get; set; } = LifetimeKind.Permanent;

    /// <summary>When it starts to apply, in the local time of the school.</summary>
    public DateTime Start { get; set; }

    /// <summary>The end of a <see cref="LifetimeKind.Range"/>, local time.</summary>
    public DateTime? End { get; set; }

    /// <summary>The lesson of a <see cref="LifetimeKind.Lesson"/>.</summary>
    public int? Period { get; set; }

    public DateTimeOffset IssuedUtc { get; set; }

    /// <summary>Who issued it (name of the console).</summary>
    public string IssuedBy { get; set; } = "";
}

/// <summary>The PC the assignments are resolved for: its identifier, its groups and, when somebody is signed in, the
/// session.</summary>
public sealed record PcTarget(string PcId, IReadOnlyCollection<string> Groups, string? SessionId = null);

/// <summary>A simple element that applies, with the assignment it comes from.</summary>
public sealed record EffectiveElement(Element Element, Assignment From);

public static class Assignments
{
    /// <summary>Whether an assignment applies at a moment (local time).</summary>
    public static bool IsActive(Assignment a, DateTime now, Timetable? timetable = null)
    {
        if (now < a.Start)
        {
            return false;
        }

        switch (a.Lifetime)
        {
            case LifetimeKind.Permanent:
                return true;
            case LifetimeKind.Range:
                return a.End is { } end && now < end;
            case LifetimeKind.UntilEndOfDay:
                return now < a.Start.Date.AddDays(1);
            case LifetimeKind.UntilEndOfWeek:
                var day = DateOnly.FromDateTime(a.Start);
                var nextMonday = a.Start.Date.AddDays(8 - Timetable.IsoWeekday(day));
                return now < nextMonday;
            case LifetimeKind.Lesson:
                if (timetable is null || a.Period is not { } number
                    || timetable.Periods.FirstOrDefault(p => p.Number == number) is not { } period)
                {
                    return false;
                }

                var from = a.Start.Date + period.Start.ToTimeSpan() - TimeSpan.FromMinutes(timetable.SwitchMinutesBefore);
                var until = a.Start.Date + period.End.ToTimeSpan();
                return now >= from && now < until;
            default:
                return false;
        }
    }

    public static bool AppliesTo(Assignment a, PcTarget pc) => a.Scope.Kind switch
    {
        ScopeKind.School => true,
        ScopeKind.Group => pc.Groups.Contains(a.Scope.Target, StringComparer.Ordinal),
        ScopeKind.Pc => a.Scope.Target == pc.PcId,
        ScopeKind.Session => pc.SessionId is { } s && a.Scope.Target == s,
        _ => false,
    };

    /// <summary>What the PC should have now: the elements of all active assignments for it, made simple; for every
    /// thing the narrowest scope wins, and of two equally narrow the later issued one. The order is from the widest
    /// scope to the narrowest, so the school comes first and the session last.</summary>
    public static List<EffectiveElement> Effective(
        IEnumerable<Assignment> assignments, PcTarget pc, DateTime now, Timetable? timetable = null)
    {
        var winners = new Dictionary<string, EffectiveElement>(StringComparer.Ordinal);
        foreach (var a in assignments.Where(a => AppliesTo(a, pc) && IsActive(a, now, timetable)))
        {
            foreach (var simple in Expansion.Expand(a.Element))
            {
                if (!winners.TryGetValue(simple.Key, out var held) || Wins(a, held.From))
                {
                    winners[simple.Key] = new EffectiveElement(simple, a);
                }
            }
        }

        return winners.Values
            .OrderBy(e => e.From.Scope.Kind)
            .ThenBy(e => e.From.IssuedUtc)
            .ThenBy(e => e.Element.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The elements of a recipe as permanent assignments for a scope (a profile given to a group, say).</summary>
    public static List<Assignment> FromRecipe(Recipe recipe, Scope scope, DateTime start, DateTimeOffset issuedUtc, string issuedBy) =>
        recipe.Elements.Select(e => new Assignment
        {
            Element = e,
            Scope = scope,
            Lifetime = LifetimeKind.Permanent,
            Start = start,
            IssuedUtc = issuedUtc,
            IssuedBy = issuedBy,
        }).ToList();

    private static bool Wins(Assignment candidate, Assignment held) =>
        candidate.Scope.Kind != held.Scope.Kind
            ? candidate.Scope.Kind > held.Scope.Kind
            : candidate.IssuedUtc > held.IssuedUtc;
}
