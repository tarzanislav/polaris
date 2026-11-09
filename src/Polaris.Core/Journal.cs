using System.Text.Json.Serialization;

namespace Polaris.Core;

/// <summary>Kinds of journal entries (stable names, they are read back by the report).</summary>
public static class JournalKind
{
    public const string Install = "instalacia";
    public const string Progress = "priebeh";
    public const string Boot = "start";
    public const string Rotation = "rotacia";
    public const string BootTime = "cas-startu";
    public const string AutoTest = "skuska";
    public const string Alarm = "poplach";
    public const string Error = "chyba";
    public const string Mode = "rezim";
    public const string Uninstall = "odinstalovanie";
    public const string Note = "poznamka";
}

/// <summary>One line of the journal (dennik.jsonl). Lines are only appended; each has its own sequence number.</summary>
public sealed class JournalEntry
{
    public long Seq { get; set; }

    public DateTimeOffset Utc { get; set; }

    public string Kind { get; set; } = JournalKind.Note;

    public Slot? Slot { get; set; }

    public string Text { get; set; } = "";

    public BootRecord? Boot { get; set; }

    /// <summary>A real percentage of a long operation (only when it is known).</summary>
    public int? Percent { get; set; }

    /// <summary>Windows' own measurement of a start (ms).</summary>
    public int? WindowsBootMs { get; set; }
}

/// <summary>What the agent found when Windows started from a layer.</summary>
public sealed class BootRecord
{
    public Slot Slot { get; set; }

    /// <summary>Starts of this layer including this one (1 = the layer was clean).</summary>
    public int SlotBoots { get; set; }

    /// <summary>The start went where the previous session prepared it.</summary>
    public bool Expected { get; set; }

    public bool MarkerFileFound { get; set; }

    public bool MarkerRegistryFound { get; set; }

    public long KernelToAgentMs { get; set; }

    public long? CycleMs { get; set; }

    public bool AutoTest { get; set; }

    public int? AutoTestIndex { get; set; }

    /// <summary>A clean start: a fresh layer and nothing from the previous session.</summary>
    [JsonIgnore]
    public bool Clean => SlotBoots == 1 && !MarkerFileFound && !MarkerRegistryFound;
}
