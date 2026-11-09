using Polaris.Core;

namespace Polaris.Tests;

public static class RotationChecks
{
    private static readonly Guid Original = new("11111111-0000-0000-0000-000000000001");

    internal static LabState NewState(LabMode mode)
    {
        var state = new LabState { InstallId = Guid.NewGuid(), OriginalBcdId = Original, Mode = mode, Phase = LabPhase.FirstBootPending };
        foreach (var slot in Slots.All)
        {
            state.Slots.Add(new SlotState { Slot = slot, BcdId = Guid.NewGuid(), File = Slots.FileName(slot) });
        }

        Rotation.ResetAll(state, Slot.A, DateTimeOffset.UnixEpoch);
        return state;
    }

    [Check]
    public static void OrderIsARing()
    {
        Expect.Equal(Slot.B, Slots.After(Slot.A), "po A");
        Expect.Equal(Slot.C, Slots.After(Slot.B), "po B");
        Expect.Equal(Slot.A, Slots.After(Slot.C), "po C");
    }

    [Check]
    public static void LivePlanMatchesTheDesign()
    {
        // design 4.2: A runs → C is made clean, next start B, default C
        var state = NewState(LabMode.Live);
        Rotation.RecordBoot(state, Slot.A, DateTimeOffset.UnixEpoch);
        state.Get(Slot.C).Boots = 1; // used in the session before
        var plan = Rotation.Plan(state, Slot.A, _ => true);
        Expect.Equal(Slot.B, plan.Next, "ďalší štart");
        Expect.Equal(state.Get(Slot.C).BcdId, plan.DefaultTarget, "predvolený");
        Expect.Sequence([Slot.C], plan.Recreate, "znovu vytvoriť");
        Expect.Sequence([state.Get(Slot.C).BcdId, Original], plan.DisplayOrder, "ponuka");
    }

    [Check]
    public static void SafePlanFallsBackToOriginal()
    {
        var state = NewState(LabMode.Safe);
        var plan = Rotation.Plan(state, Slot.A, _ => true);
        Expect.Equal(Original, plan.DefaultTarget, "predvolený = pôvodný Windows");
        Expect.Sequence([Original], plan.DisplayOrder, "ponuka");
    }

    [Check]
    public static void MissingFileIsRecreated()
    {
        var state = NewState(LabMode.Live);
        var plan = Rotation.Plan(state, Slot.A, s => s != Slot.B);
        Expect.Sequence([Slot.B], plan.Recreate, "chýbajúci súbor");
    }

    /// <summary>Walks many cycles with random power losses in every step of the agent, in the order the agent really
    /// takes them, and checks the promise of the design: a used layer is started only after a double fault – a power
    /// loss in the first seconds of a session that itself replaced a crashed one – and then it is always recognized
    /// (the agent restarts it at once into a clean layer, nobody works in it).</summary>
    [Check]
    public static void PowerLossNeverLeavesAUsedLayerUnnoticed()
    {
        var random = new Random(20261110);
        foreach (var mode in new[] { LabMode.Live, LabMode.Safe })
        {
            var state = NewState(mode);
            Guid? bootSequence = state.Get(Slot.A).BcdId;
            var defaultEntry = Original;
            var original = 0;
            var used = 0;
            var doubleFault = false;
            for (var cycle = 0; cycle < 20000; cycle++)
            {
                // the boot manager: a one-time start wins and is used up, else the default
                var target = bootSequence ?? defaultEntry;
                bootSequence = null;
                if (target == Original)
                {
                    Expect.True(mode == LabMode.Safe, "pôvodný Windows len v bezpečnom režime");
                    original++;
                    // the owner resumes from the original Windows: all layers clean, next start A
                    Rotation.ResetAll(state, Slot.A, DateTimeOffset.UnixEpoch);
                    bootSequence = state.Get(Slot.A).BcdId;
                    doubleFault = false;
                    continue;
                }

                var running = Rotation.RunningSlot(state, target)!.Value;
                if (state.Get(running).Boots > 0)
                {
                    Expect.True(mode == LabMode.Live && doubleFault, $"cyklus {cycle}: použitá vrstva {running} bez dvojitej poruchy");
                    used++;
                }

                // a power loss counts only while the default still points to the running layer
                bool Lost(int oneIn) => cycle > 0 && random.Next(oneIn) == 0;
                doubleFault = false;
                if (Lost(5))
                {
                    // before the agent: the layer was used, the agent did nothing
                    state.Get(running).Boots++;
                    doubleFault = defaultEntry == target;
                    continue;
                }

                Rotation.RecordBoot(state, running, DateTimeOffset.UnixEpoch);
                var plan = Rotation.Plan(state, running, _ => true);
                // step 1: the third layer clean, and at once the default moves to it
                if (plan.Recreate.Contains(plan.Third))
                {
                    if (Lost(10))
                    {
                        doubleFault = defaultEntry == target;
                        continue;
                    }

                    Rotation.MarkRecreated(state, plan.Third, DateTimeOffset.UnixEpoch);
                }

                defaultEntry = plan.DefaultTarget;
                // step 2: the next layer clean, then the one-time start to it
                if (plan.Recreate.Contains(plan.Next))
                {
                    if (Lost(10))
                    {
                        continue;
                    }

                    Rotation.MarkRecreated(state, plan.Next, DateTimeOffset.UnixEpoch);
                }

                bootSequence = state.Get(plan.Next).BcdId;
                Rotation.MarkApplied(state, plan);
            }

            Expect.True(mode == LabMode.Safe ? original > 0 && used == 0 : original == 0 && used > 0, "návraty do pôvodného Windows");
        }
    }

    [Check]
    public static void BootIsCountedAndPhaseMoves()
    {
        var state = NewState(LabMode.Safe);
        Expect.Equal(1, Rotation.RecordBoot(state, Slot.A, DateTimeOffset.UnixEpoch), "prvý štart");
        Expect.Equal(LabPhase.Running, state.Phase, "fáza");
        Expect.True(Rotation.IsExpected(state, Slot.A), "A bola pripravená");
        Expect.True(!Rotation.IsExpected(state, Slot.B), "B nebola pripravená");
        Expect.Equal(2, Rotation.RecordBoot(state, Slot.A, DateTimeOffset.UnixEpoch), "druhý štart tej istej vrstvy");
    }

    [Check]
    public static void StateSurvivesJson()
    {
        var state = NewState(LabMode.Live);
        state.AutoTest = new AutoTestState { Active = true, Total = 100, Remaining = 7 };
        state.PhysicalBootTimesMs = [21000, 23000];
        var back = Serialization.StateFromJson(Serialization.ToJson(state));
        Expect.Equal(state.InstallId, back.InstallId, "identifikátor");
        Expect.Equal(LabMode.Live, back.Mode, "režim");
        Expect.Equal(Slot.A, back.ExpectedNext, "ďalší štart");
        Expect.Equal(7, back.AutoTest.Remaining, "skúška");
        Expect.Sequence(state.Slots.Select(s => s.BcdId), back.Slots.Select(s => s.BcdId), "vrstvy");
        Expect.True(Serialization.ToJson(state).Contains("\"mode\": \"Live\"", StringComparison.Ordinal), "režim ako text");
    }
}
