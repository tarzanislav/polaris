using System.Globalization;
using Polaris.Core;
using Polaris.Json;

namespace Polaris.Recipes;

/// <summary>A profile in a plan: its name and the fingerprint of the exact recipe approved for it.</summary>
public sealed record PlannedProfile(string Name, string Fingerprint);

/// <summary>What one group of PCs should run and when: the profiles (each pinned to one recipe by its fingerprint), the
/// default profile and the group's part of the timetable. A console signs it; a PC takes only a plan signed by a console
/// it trusts and never an older one than it has.</summary>
public sealed class GroupPlan
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    public string Group { get; set; } = "";

    /// <summary>Grows with every publication (milliseconds since 1970 at publishing), also across several consoles.</summary>
    public long Sequence { get; set; }

    public DateTimeOffset IssuedUtc { get; set; }

    public string DefaultProfile { get; set; } = "";

    public List<PlannedProfile> Profiles { get; set; } = [];

    public Timetable Timetable { get; set; } = new();

    public PlannedProfile? Find(string name) => Profiles.FirstOrDefault(p => p.Name == name);

    /// <summary>The profile the group should run at a local moment.</summary>
    public string ProfileAt(DateTime local) => Timetable.ProfileAt(local, Group, DefaultProfile);

    /// <summary>The plan of a group taken out of the centre's settings: only its lessons and changes, the bells, the days off.</summary>
    public static GroupPlan For(Classroom c, string group, IReadOnlyDictionary<string, string> fingerprints, DateTimeOffset now)
    {
        var g = c.Groups.FirstOrDefault(x => x.Name == group) ?? throw new InvalidDataException($"Skupina {group} neexistuje.");
        var t = new Timetable
        {
            SwitchMinutesBefore = c.Timetable.SwitchMinutesBefore,
            Periods = [.. c.Timetable.Periods],
            Lessons = c.Timetable.Lessons.Where(l => l.Group == group).ToList(),
            Overrides = c.Timetable.Overrides.Where(o => o.Group == group && o.Date >= DateOnly.FromDateTime(now.LocalDateTime).AddDays(-1)).ToList(),
            DaysOff = c.Timetable.DaysOff.Where(d => d >= DateOnly.FromDateTime(now.LocalDateTime).AddDays(-1)).ToHashSet(),
        };
        var used = t.Lessons.Select(l => l.Profile).Concat(t.Overrides.Select(o => o.Profile)).Append(g.DefaultProfile).Distinct().Order(StringComparer.Ordinal);
        return new GroupPlan
        {
            Group = group,
            Sequence = now.ToUnixTimeMilliseconds(),
            IssuedUtc = now.ToUniversalTime(),
            DefaultProfile = g.DefaultProfile,
            Profiles = used.Select(name => new PlannedProfile(name, fingerprints.TryGetValue(name, out var f) ? f : throw new InvalidDataException($"Profil {name} nemá podpísaný recept."))).ToList(),
            Timetable = t,
        };
    }
}

/// <summary>The written form of a plan; its fingerprint is the SHA-256 of the canonical form (what a console signs).</summary>
public static class GroupPlanFormat
{
    public static JsonObject Write(GroupPlan p) => new JsonObject()
        .Add("schema", p.Schema)
        .Add("group", p.Group)
        .Add("sequence", p.Sequence)
        .Add("issuedUtc", p.IssuedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        .Add("defaultProfile", p.DefaultProfile)
        .Add("profiles", JsonArray.Of(p.Profiles, x => new JsonObject().Add("name", x.Name).Add("fingerprint", x.Fingerprint)))
        .Add("timetable", ClassroomFormat.Timetable(p.Timetable));

    public static string Fingerprint(GroupPlan p) => JsonText.Fingerprint(Write(p));

    public static GroupPlan Read(JsonNode node)
    {
        var f = new JsonFields(node, "plán skupiny");
        var schema = f.Int("schema", GroupPlan.CurrentSchema);
        if (schema > GroupPlan.CurrentSchema)
        {
            throw new InvalidDataException($"Plán je z novšieho centra (schéma {schema}) – aktualizuj agenta.");
        }

        var p = new GroupPlan
        {
            Schema = schema,
            Group = f.String("group"),
            Sequence = f.Long("sequence", 0, 0, long.MaxValue),
            IssuedUtc = f.Time("issuedUtc"),
            DefaultProfile = f.String("defaultProfile"),
            Profiles = f.Array("profiles").Select(n =>
            {
                var x = new JsonFields(n, "profil plánu");
                var planned = new PlannedProfile(x.String("name"), x.String("fingerprint"));
                x.End();
                return planned;
            }).ToList(),
            Timetable = f.Fields("timetable", "rozvrh") is { } t ? ClassroomFormat.ReadTimetable(t) : new Timetable(),
        };
        f.End();
        if (p.Find(p.DefaultProfile) is null)
        {
            throw new InvalidDataException("Plán nemá recept pre predvolený profil.");
        }

        return p;
    }
}
