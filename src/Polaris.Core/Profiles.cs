namespace Polaris.Core;

/// <summary>What happens to the changes of a session in this profile.</summary>
public enum ProfilePolicy
{
    /// <summary>Every restart returns to the clean version (classroom profiles).</summary>
    Frozen,

    /// <summary>Changes stay until the admin drops them (e.g. the admin's own profile).</summary>
    Persistent,
}

public enum VersionState
{
    /// <summary>Being prepared in service mode; not usable yet.</summary>
    Draft,

    /// <summary>Checked and approved – it may run on PCs.</summary>
    Approved,

    /// <summary>Old, no longer kept on the PCs.</summary>
    Retired,
}

/// <summary>A program installed in a version (for comparing two versions).</summary>
public sealed record InstalledApp(string Name, string Version);

/// <summary>One version of a profile = one point of restore in time.</summary>
public sealed class ProfileVersion
{
    public int Number { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>What changed, written by the admin (e.g. "aktualizácie november, GIMP 3.2").</summary>
    public string Note { get; set; } = "";

    /// <summary>The version it was made from (0 = the first).</summary>
    public int BasedOn { get; set; }

    public VersionState State { get; set; } = VersionState.Draft;

    /// <summary>Fingerprint of the content (SHA-256), so every PC can check it has exactly this version.</summary>
    public string Sha256 { get; set; } = "";

    public List<InstalledApp> Apps { get; set; } = [];
}

/// <summary>The difference between two versions, for the admin to read before switching.</summary>
public sealed record VersionDiff(
    IReadOnlyList<InstalledApp> Added,
    IReadOnlyList<InstalledApp> Removed,
    IReadOnlyList<(string Name, string From, string To)> Changed);

/// <summary>A profile (KANCELÁRSKY, GRAFICKÝ, SPRÁVCA …) with its versions in time. The rules: a draft is never active,
/// at least the active and the previous approved version are always kept, so going back is always possible.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public ProfilePolicy Policy { get; set; } = ProfilePolicy.Frozen;

    public List<ProfileVersion> Versions { get; set; } = [];

    /// <summary>The version PCs of this profile run.</summary>
    public int Active { get; set; }

    /// <summary>How many approved versions are kept (never fewer than 2).</summary>
    public int Keep { get; set; } = 2;

    public ProfileVersion? Get(int number) => Versions.FirstOrDefault(v => v.Number == number);

    /// <summary>The time axis: all versions, oldest first.</summary>
    public IEnumerable<ProfileVersion> Timeline => Versions.OrderBy(v => v.Number);

    /// <summary>Starts a new version from the active one (service mode); it is a draft until approved.</summary>
    public ProfileVersion NewDraft(string note, DateTimeOffset now)
    {
        if (Versions.Any(v => v.State == VersionState.Draft))
        {
            throw new InvalidOperationException($"Profil {Name} už má rozpracovanú verziu.");
        }

        var draft = new ProfileVersion
        {
            Number = Versions.Count == 0 ? 1 : Versions.Max(v => v.Number) + 1,
            CreatedUtc = now,
            Note = note,
            BasedOn = Active,
            State = VersionState.Draft,
            Apps = Get(Active)?.Apps.ToList() ?? [],
        };
        Versions.Add(draft);
        return draft;
    }

    /// <summary>Approves a draft (after its check) and makes it active; old versions beyond the kept count retire.</summary>
    public void Approve(int number, string sha256)
    {
        var v = Get(number) ?? throw new InvalidOperationException($"Verzia {number} profilu {Name} neexistuje.");
        if (v.State != VersionState.Draft)
        {
            throw new InvalidOperationException($"Verzia {number} nie je rozpracovaná.");
        }

        if (sha256.Length != 64)
        {
            throw new ArgumentException("Odtlačok verzie musí byť SHA-256 (64 znakov).", nameof(sha256));
        }

        v.Sha256 = sha256;
        v.State = VersionState.Approved;
        Active = number;
        RetireOld();
    }

    /// <summary>Drops a draft (the service changes are thrown away).</summary>
    public void DropDraft()
    {
        Versions.RemoveAll(v => v.State == VersionState.Draft);
    }

    /// <summary>Switches to an approved version (a point in time).</summary>
    public void Activate(int number)
    {
        var v = Get(number) ?? throw new InvalidOperationException($"Verzia {number} profilu {Name} neexistuje.");
        if (v.State != VersionState.Approved)
        {
            throw new InvalidOperationException($"Verzia {number} nie je schválená – spustiť sa nedá.");
        }

        Active = number;
    }

    /// <summary>Goes back to the newest approved version older than the active one.</summary>
    public int RollBack()
    {
        var previous = Versions
            .Where(v => v.State == VersionState.Approved && v.Number < Active)
            .OrderByDescending(v => v.Number)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Profil {Name} nemá staršiu schválenú verziu.");
        Active = previous.Number;
        return Active;
    }

    /// <summary>What the second version has compared with the first (apps added, removed, in another version).</summary>
    public static VersionDiff Compare(ProfileVersion from, ProfileVersion to)
    {
        var a = from.Apps.ToDictionary(x => x.Name, x => x.Version, StringComparer.OrdinalIgnoreCase);
        var b = to.Apps.ToDictionary(x => x.Name, x => x.Version, StringComparer.OrdinalIgnoreCase);
        var added = to.Apps.Where(x => !a.ContainsKey(x.Name)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = from.Apps.Where(x => !b.ContainsKey(x.Name)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var changed = to.Apps
            .Where(x => a.TryGetValue(x.Name, out var old) && old != x.Version)
            .Select(x => (x.Name, a[x.Name], x.Version))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new VersionDiff(added, removed, changed);
    }

    private void RetireOld()
    {
        var keep = Math.Max(2, Keep);
        var approved = Versions.Where(v => v.State == VersionState.Approved).OrderByDescending(v => v.Number).ToList();
        foreach (var v in approved.Skip(keep))
        {
            if (v.Number != Active)
            {
                v.State = VersionState.Retired;
            }
        }
    }
}
