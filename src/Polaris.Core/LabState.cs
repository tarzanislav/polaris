using System.Text.Json.Serialization;

namespace Polaris.Core;

/// <summary>Safe: when anything goes wrong the PC falls back to the original Windows. Live: it always starts a clean layer.</summary>
public enum LabMode
{
    Safe,
    Live,
}

public enum LabPhase
{
    /// <summary>The conversion is running; the layers are not usable yet.</summary>
    Preparing,

    /// <summary>The layers are ready and the first start from layer A was requested.</summary>
    FirstBootPending,

    /// <summary>The PC has started from a layer at least once.</summary>
    Running,

    /// <summary>The boot entries were removed; the files are deleted from the original Windows.</summary>
    Uninstalling,
}

/// <summary>The whole persistent state of the laboratory, kept in the store (stav.json) on the host volume.</summary>
public sealed class LabState
{
    public int Schema { get; set; } = 1;

    public Guid InstallId { get; set; }

    public string ProgramVersion { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }

    public LabPhase Phase { get; set; }

    public LabMode Mode { get; set; } = LabMode.Safe;

    /// <summary>The boot entry of the original (physical) Windows.</summary>
    public Guid OriginalBcdId { get; set; }

    /// <summary>The boot manager settings before the installation, restored by the uninstallation.</summary>
    public BootManagerSettings OriginalBoot { get; set; } = new();

    public string BaseFile { get; set; } = "zaklad.vhdx";

    /// <summary>The GPT partition inside the base layer (C: of the layered Windows).</summary>
    public Guid BasePartitionId { get; set; }

    public long SourceUsedBytes { get; set; }

    public long CopiedBytes { get; set; }

    public long BaseFileBytes { get; set; }

    public long CopySeconds { get; set; }

    public List<SlotState> Slots { get; set; } = [];

    /// <summary>The slot the next start goes to (one-time start).</summary>
    public Slot? ExpectedNext { get; set; }

    /// <summary>Where the boot manager goes when the one-time start was used up (after a crash or power loss).</summary>
    public Guid DefaultTarget { get; set; }

    public long GenerationCounter { get; set; }

    public long JournalSeq { get; set; }

    public AutoTestState AutoTest { get; set; } = new();

    /// <summary>When the last restart was asked for – the length of a whole cycle is measured from it.</summary>
    public DateTimeOffset? LastRestartRequestUtc { get; set; }

    /// <summary>Start times of the original Windows read from its own measurements (ms), for comparison.</summary>
    public List<int> PhysicalBootTimesMs { get; set; } = [];

    public SlotState Get(Slot slot) => Slots.First(s => s.Slot == slot);

    public SlotState? FindByBcdId(Guid id) => Slots.FirstOrDefault(s => s.BcdId == id);
}

public sealed class SlotState
{
    public Slot Slot { get; set; }

    public Guid BcdId { get; set; }

    public string File { get; set; } = "";

    /// <summary>How many times Windows started from this layer since it was created clean.</summary>
    public int Boots { get; set; }

    public long Generation { get; set; }

    public DateTimeOffset? CreatedUtc { get; set; }

    public DateTimeOffset? LastBootUtc { get; set; }
}

public sealed class BootManagerSettings
{
    public Guid? Default { get; set; }

    public List<Guid> DisplayOrder { get; set; } = [];

    public List<Guid> BootSequence { get; set; } = [];

    public long? Timeout { get; set; }
}

public sealed class AutoTestState
{
    /// <summary>The test runs: every start is counted as a start of the test.</summary>
    public bool Active { get; set; }

    public int Total { get; set; }

    /// <summary>Restarts still to be done by the agent.</summary>
    public int Remaining { get; set; }

    /// <summary>How long each start waits before the next restart (seconds).</summary>
    public int WaitSeconds { get; set; } = 120;

    public DateTimeOffset? StartedUtc { get; set; }

    public string? StopReason { get; set; }

    /// <summary>The number of the start in the test (1 = the first start after the test was ordered).</summary>
    [JsonIgnore]
    public int CurrentIndex => Total - Remaining;

    public void Stop(string reason)
    {
        Active = false;
        Remaining = 0;
        StopReason = reason;
    }
}
