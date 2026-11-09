using Polaris.Core;

namespace Polaris.Tests;

public static class ReportChecks
{
    private static JournalEntry BootEntry(long seq, Slot slot, bool clean, bool auto)
    {
        var record = new BootRecord
        {
            Slot = slot,
            SlotBoots = clean ? 1 : 2,
            Expected = true,
            AutoTest = auto,
            KernelToAgentMs = 9000,
            CycleMs = 70_000,
        };
        return new JournalEntry { Seq = seq, Kind = JournalKind.Boot, Slot = slot, Boot = record, Text = "štart" };
    }

    [Check]
    public static void GateNeedsHundredCleanBoots()
    {
        var journal = Enumerable.Range(0, 99).Select(i => BootEntry(i, Slots.All[i % 3], true, true)).ToList();
        Expect.True(!Report.Gate(journal).Passed, "99 nestačí");
        journal.Add(BootEntry(99, Slot.A, true, true));
        Expect.True(Report.Gate(journal).Passed, "100 stačí");
        journal.Add(BootEntry(100, Slot.B, false, false));
        var gate = Report.Gate(journal);
        Expect.True(!gate.Passed, "jeden nečistý štart bránu zastaví");
        Expect.True(gate.Problems[0].Contains("už bežala", StringComparison.Ordinal), "dôvod");
    }

    [Check]
    public static void ReportIsReadable()
    {
        var state = RotationChecks.NewState(LabMode.Safe);
        state.SourceUsedBytes = 30_000_000_000;
        state.PhysicalBootTimesMs = [20000, 22000];
        var journal = new List<JournalEntry>
        {
            BootEntry(1, Slot.A, true, false),
            new() { Seq = 2, Kind = JournalKind.BootTime, WindowsBootMs = 23100, Text = "čas" },
        };
        var text = Report.Build(state, journal, DateTimeOffset.UnixEpoch);
        Expect.True(text.Contains("ŠTARTY Z VRSTIEV", StringComparison.Ordinal), "štarty");
        Expect.True(text.Contains("spolu: 1", StringComparison.Ordinal), "počet");
        Expect.True(text.Contains("+10,0 %", StringComparison.Ordinal), "porovnanie s pôvodným");
        Expect.True(text.Contains("ZATIAĽ NESPLNENÁ", StringComparison.Ordinal), "brána");
    }

    [Check]
    public static void JournalSurvivesCutLine()
    {
        var a = Serialization.ToLine(BootEntry(1, Slot.A, true, true));
        var b = Serialization.ToLine(BootEntry(2, Slot.B, true, true));
        var text = a + "\n" + b + "\n" + b[..(b.Length / 2)];
        var read = Serialization.ReadJournal(text);
        Expect.Equal(2, read.Count, "odrezaný posledný riadok sa preskočí");
        Expect.Equal(Slot.B, read[1].Boot!.Slot, "vrstva");
    }
}
