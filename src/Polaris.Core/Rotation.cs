namespace Polaris.Core;

/// <summary>What has to happen while Windows runs from <see cref="Running"/>: which layers to make clean again and
/// where the next starts go.</summary>
/// <param name="Running">The layer Windows runs from now.</param>
/// <param name="Next">The one-time next start (a clean layer).</param>
/// <param name="Third">The remaining layer – in the live mode the default start after a crash.</param>
/// <param name="Recreate">Layers that were used (or are missing) and must be made clean before anything else.</param>
/// <param name="DefaultTarget">The default start: the third clean layer (live) or the original Windows (safe).</param>
/// <param name="DisplayOrder">The boot menu shown only when the one-time start was used up.</param>
public sealed record RotationPlan(
    Slot Running,
    Slot Next,
    Slot Third,
    IReadOnlyList<Slot> Recreate,
    Guid DefaultTarget,
    IReadOnlyList<Guid> DisplayOrder);

/// <summary>The heart of the freezing: three session layers in a ring. The rule – the default start never points to a
/// layer that runs or has run – means that a power loss, a crash or a killed agent always ends in a clean layer.</summary>
public static class Rotation
{
    public static RotationPlan Plan(LabState state, Slot running, Func<Slot, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fileExists);
        var next = Slots.After(running);
        var third = Slots.After(next);
        var recreate = new List<Slot>();
        foreach (var slot in new[] { next, third })
        {
            if (state.Get(slot).Boots > 0 || !fileExists(slot))
            {
                recreate.Add(slot);
            }
        }

        var defaultTarget = state.Mode == LabMode.Live ? state.Get(third).BcdId : state.OriginalBcdId;
        IReadOnlyList<Guid> order = defaultTarget == state.OriginalBcdId
            ? [state.OriginalBcdId]
            : [defaultTarget, state.OriginalBcdId];
        return new RotationPlan(running, next, third, recreate, defaultTarget, order);
    }

    /// <summary>Recognizes the layer Windows started from by the identifier of its boot entry.</summary>
    public static Slot? RunningSlot(LabState state, Guid bootIdentifier) => state.FindByBcdId(bootIdentifier)?.Slot;

    /// <summary>Was this start the one the previous session prepared (one-time start, or the default after a crash)?</summary>
    public static bool IsExpected(LabState state, Slot running) =>
        state.ExpectedNext == running || state.DefaultTarget == state.Get(running).BcdId;

    /// <summary>Counts a start of a layer. Returns the number of starts including this one (1 = the layer was clean).</summary>
    public static int RecordBoot(LabState state, Slot running, DateTimeOffset now)
    {
        var slot = state.Get(running);
        slot.Boots++;
        slot.LastBootUtc = now;
        if (state.Phase == LabPhase.FirstBootPending)
        {
            state.Phase = LabPhase.Running;
        }

        return slot.Boots;
    }

    /// <summary>A layer was made clean again (a new empty difference layer above the base).</summary>
    public static void MarkRecreated(LabState state, Slot slot, DateTimeOffset now)
    {
        var s = state.Get(slot);
        s.Boots = 0;
        s.Generation = ++state.GenerationCounter;
        s.CreatedUtc = now;
    }

    /// <summary>The boot manager now matches the plan.</summary>
    public static void MarkApplied(LabState state, RotationPlan plan)
    {
        state.ExpectedNext = plan.Next;
        state.DefaultTarget = plan.DefaultTarget;
    }

    /// <summary>All layers are made clean (they are not in use) and the next start goes to <paramref name="first"/>.
    /// Used by the conversion and by a restart from the original Windows.</summary>
    public static void ResetAll(LabState state, Slot first, DateTimeOffset now)
    {
        foreach (var slot in Slots.All)
        {
            MarkRecreated(state, slot, now);
        }

        state.ExpectedNext = first;
        state.DefaultTarget = state.OriginalBcdId;
    }
}
