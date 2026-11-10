namespace Polaris.Net;

/// <summary>The fixed list of tasks a console may send to a PC. The network never carries an arbitrary command – only
/// one of these, and only signed. Anything a message asks that is not on this list is refused.</summary>
public enum TaskKind
{
    /// <summary>Restart the PC (into a clean layer).</summary>
    Restart,

    /// <summary>Shut the PC down.</summary>
    ShutDown,

    /// <summary>Wake the PC over the network (Wake-on-LAN).</summary>
    WakeUp,

    /// <summary>Freeze: every restart returns to the clean state.</summary>
    Freeze,

    /// <summary>Thaw for a given number of restarts (changes survive that many starts).</summary>
    ThawForRestarts,

    /// <summary>Pause the protection until a given moment – the PC is NOT protected meanwhile.</summary>
    Pause,

    /// <summary>Enter service mode (changes survive until saved as a version or dropped).</summary>
    Service,

    /// <summary>Save the service changes as a new version of the profile.</summary>
    SaveServiceAsVersion,

    /// <summary>Drop the service changes (as if nothing happened).</summary>
    DropService,

    /// <summary>Switch the PC to another profile.</summary>
    SwitchProfile,

    /// <summary>Switch the PC to another version of its profile (a point in time).</summary>
    SwitchVersion,

    /// <summary>Go back to the previous version of the profile.</summary>
    RollBackVersion,

    /// <summary>Hand out fresh antivirus definitions to load after the next start.</summary>
    FreshDefinitions,

    /// <summary>Show the pupil a short message.</summary>
    MessageToPupil,

    /// <summary>Collect the pupils' work before a restore.</summary>
    CollectWork,
}

public static class Tasks
{
    /// <summary>Which role may send a task. A teacher may do only the gentle, classroom things.</summary>
    public static bool AllowedForTeacher(TaskKind kind) => kind is
        TaskKind.Restart or TaskKind.WakeUp or TaskKind.SwitchProfile or TaskKind.MessageToPupil or TaskKind.CollectWork;

    public static string Describe(TaskKind kind) => kind switch
    {
        TaskKind.Restart => "reštartovať",
        TaskKind.ShutDown => "vypnúť",
        TaskKind.WakeUp => "zobudiť",
        TaskKind.Freeze => "zmraziť",
        TaskKind.ThawForRestarts => "rozmraziť na N reštartov",
        TaskKind.Pause => "pozastaviť ochranu",
        TaskKind.Service => "servisný režim",
        TaskKind.SaveServiceAsVersion => "uložiť servis ako verziu",
        TaskKind.DropService => "zahodiť servis",
        TaskKind.SwitchProfile => "prepnúť profil",
        TaskKind.SwitchVersion => "prepnúť verziu",
        TaskKind.RollBackVersion => "vrátiť predošlú verziu",
        TaskKind.FreshDefinitions => "čerstvé definície antivírusu",
        TaskKind.MessageToPupil => "správa žiakovi",
        TaskKind.CollectWork => "zber prác",
        _ => "neznáma úloha",
    };

    /// <summary>Only a value of the fixed list is a task; any other number is refused, even when signed.</summary>
    public static bool IsKnown(TaskKind kind) => Enum.IsDefined(kind);
}
