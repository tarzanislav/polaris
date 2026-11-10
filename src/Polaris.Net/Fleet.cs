namespace Polaris.Net;

/// <summary>The role a console key is given. A teacher may do only the gentle classroom tasks; read-only may do nothing
/// but look.</summary>
public enum Role
{
    ReadOnly,
    Teacher,
    Admin,
}

/// <summary>A console allowed to talk to the centre: its key fingerprint, a name and a role. Removing it takes effect at
/// once (the centre checks the list on every request).</summary>
public sealed class ConsoleAccess
{
    public string Fingerprint { get; set; } = "";

    public string Name { get; set; } = "";

    public Role Role { get; set; } = Role.ReadOnly;

    /// <summary>The public key (subject public key info), so the centre can check signatures.</summary>
    public byte[] PublicKey { get; set; } = [];

    public bool MaySend(TaskKind kind) => Role switch
    {
        Role.Admin => true,
        Role.Teacher => Tasks.AllowedForTeacher(kind),
        _ => false,
    };
}

/// <summary>A PC in the classroom, as the centre knows it. No individual facts are stored – no computer name chosen by a
/// person, no serial numbers in the repository; those arrive only at install time into the centre's own settings.</summary>
public sealed class Pc
{
    public Guid Id { get; set; }

    /// <summary>The name shown in the console (given at the centre, not in the code).</summary>
    public string Name { get; set; } = "";

    public string Group { get; set; } = "";

    /// <summary>The profile the PC should run.</summary>
    public string Profile { get; set; } = "";

    public PcState State { get; set; } = new();

    /// <summary>Last time the PC reported in (UTC). A PC silent too long counts as off.</summary>
    public DateTimeOffset? LastSeenUtc { get; set; }

    public bool OnlineAt(DateTimeOffset now, TimeSpan silence) =>
        LastSeenUtc is { } seen && now - seen <= silence;
}

/// <summary>What a PC last reported about itself.</summary>
public sealed class PcState
{
    public string Phase { get; set; } = "";

    public string RunningProfile { get; set; } = "";

    public string RunningVersion { get; set; } = "";

    public bool Frozen { get; set; }

    /// <summary>When paused: the protection is off until this moment.</summary>
    public DateTimeOffset? PausedUntilUtc { get; set; }

    public int CleanBoots { get; set; }
}

/// <summary>A group of PCs of the same hardware; settings go all → group → PC, the narrower wins.</summary>
public sealed class Group
{
    public string Name { get; set; } = "";

    public string DefaultProfile { get; set; } = "";
}

/// <summary>All PCs, groups and consoles the centre knows. Settings resolve narrowest-first.</summary>
public sealed class Fleet
{
    public List<Pc> Pcs { get; set; } = [];

    public List<Group> Groups { get; set; } = [];

    public List<ConsoleAccess> Consoles { get; set; } = [];

    public Pc? Find(Guid id) => Pcs.FirstOrDefault(p => p.Id == id);

    public ConsoleAccess? FindConsole(string fingerprint) => Consoles.FirstOrDefault(c => c.Fingerprint == fingerprint);

    /// <summary>The profile a PC should run: its own if set, else its group's, else empty.</summary>
    public string ProfileFor(Pc pc)
    {
        if (pc.Profile.Length > 0)
        {
            return pc.Profile;
        }

        return Groups.FirstOrDefault(g => g.Name == pc.Group)?.DefaultProfile ?? "";
    }

    public IEnumerable<Pc> InGroup(string group) => Pcs.Where(p => p.Group == group);
}
