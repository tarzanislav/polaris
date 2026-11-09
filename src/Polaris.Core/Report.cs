using System.Text;

namespace Polaris.Core;

/// <summary>The verdict of the gate of stage E0.</summary>
public sealed record GateVerdict(bool Passed, int CleanAutoTestBoots, int Required, IReadOnlyList<string> Problems);

/// <summary>The report of the laboratory: what the conversion did, every start from a layer, the test of restarts and
/// the verdict of the gate. Plain text, readable in Notepad and printable.</summary>
public static class Report
{
    public const int GateBoots = 100;

    public static GateVerdict Gate(IEnumerable<JournalEntry> journal)
    {
        var boots = journal.Where(e => e.Kind == JournalKind.Boot && e.Boot is not null).Select(e => e.Boot!).ToList();
        var problems = new List<string>();
        foreach (var b in boots)
        {
            if (!b.Clean)
            {
                problems.Add($"vrstva {b.Slot}: nečistý štart ({Why(b)})");
            }
            else if (!b.Expected)
            {
                problems.Add($"vrstva {b.Slot}: štart mimo prípravy");
            }
        }

        var clean = boots.Count(b => b.AutoTest && b.Clean && b.Expected);
        return new GateVerdict(problems.Count == 0 && clean >= GateBoots, clean, GateBoots, problems);
    }

    public static string Why(BootRecord b)
    {
        var reasons = new List<string>();
        if (b.SlotBoots != 1)
        {
            reasons.Add($"vrstva už bežala {b.SlotBoots - 1}×");
        }

        if (b.MarkerFileFound)
        {
            reasons.Add("ostal skúšobný súbor");
        }

        if (b.MarkerRegistryFound)
        {
            reasons.Add("ostal skúšobný zápis v registri");
        }

        return reasons.Count == 0 ? "bez nálezu" : string.Join(", ", reasons);
    }

    public static string Build(LabState state, IReadOnlyList<JournalEntry> journal, DateTimeOffset now)
    {
        var t = new StringBuilder();
        t.AppendLine($"{Product.Name} – laboratórium {Product.Stage} – správa");
        t.AppendLine($"Vytvorená: {Format.Local(now)}   Program: {Product.Version}");
        t.AppendLine(new string('=', 72));
        t.AppendLine($"Inštalácia:  {Format.Local(state.CreatedUtc)} (program {state.ProgramVersion})");
        t.AppendLine($"Fáza:        {PhaseText(state.Phase)}");
        t.AppendLine($"Režim:       {ModeText(state.Mode)}");
        t.AppendLine($"Základ:      použité dáta {Format.Gb(state.SourceUsedBytes)}, skopírované {Format.Gb(state.CopiedBytes)}, "
            + $"súbor {Format.Gb(state.BaseFileBytes)}, prevod {Format.Duration(TimeSpan.FromSeconds(state.CopySeconds))}");
        t.AppendLine();

        var boots = journal.Where(e => e.Kind == JournalKind.Boot && e.Boot is not null).ToList();
        t.AppendLine("ŠTARTY Z VRSTIEV");
        t.AppendLine($"  spolu: {boots.Count}  (A: {boots.Count(e => e.Boot!.Slot == Slot.A)}, B: {boots.Count(e => e.Boot!.Slot == Slot.B)}, "
            + $"C: {boots.Count(e => e.Boot!.Slot == Slot.C)})");
        t.AppendLine($"  čisté: {boots.Count(e => e.Boot!.Clean)}, nečisté: {boots.Count(e => !e.Boot!.Clean)}");
        t.AppendLine($"  podľa prípravy: {boots.Count(e => e.Boot!.Expected)}, mimo prípravy: {boots.Count(e => !e.Boot!.Expected)}");
        var alarms = journal.Count(e => e.Kind == JournalKind.Alarm);
        t.AppendLine($"  poplachy: {alarms}");
        t.AppendLine();

        var auto = boots.Where(e => e.Boot!.AutoTest).Select(e => e.Boot!).ToList();
        t.AppendLine("SKÚŠKA REŠTARTOV");
        if (state.AutoTest.Total == 0 && auto.Count == 0)
        {
            t.AppendLine("  ešte nebola spustená");
        }
        else
        {
            var status = state.AutoTest.Active ? "prebieha"
                : state.AutoTest.StopReason is { } reason ? "zastavená – " + reason
                : "dokončená";
            t.AppendLine($"  stav: {status}; hotové štarty: {auto.Count}, objednané: {state.AutoTest.Total}");
        }

        AppendStats(t, "  celý cyklus (žiadosť o reštart → agent)", boots.Select(e => e.Boot!.CycleMs).OfType<long>());
        AppendStats(t, "  od štartu jadra po agenta", boots.Select(e => e.Boot!.KernelToAgentMs).Where(v => v > 0));
        var layered = journal.Where(e => e.Kind == JournalKind.BootTime && e.WindowsBootMs is > 0).Select(e => (long)e.WindowsBootMs!.Value).ToList();
        AppendStats(t, "  štart podľa Windows – vrstva", layered);
        AppendStats(t, "  štart podľa Windows – pôvodný", state.PhysicalBootTimesMs.Select(v => (long)v));
        if (layered.Count > 0 && state.PhysicalBootTimesMs.Count > 0)
        {
            var ratio = layered.Average() / state.PhysicalBootTimesMs.Average() - 1;
            t.AppendLine($"  rozdiel vrstva oproti pôvodnému: {(ratio >= 0 ? "+" : "")}{Format.Decimal(ratio * 100, 1)} % (cieľ najviac +15 %)");
        }

        t.AppendLine();
        var gate = Gate(journal);
        t.AppendLine($"BRÁNA {Product.Stage}: {(gate.Passed ? "SPLNENÁ" : "ZATIAĽ NESPLNENÁ")}");
        t.AppendLine($"  čisté štarty skúšky: {gate.CleanAutoTestBoots} z potrebných {gate.Required}");
        foreach (var p in gate.Problems.Take(20))
        {
            t.AppendLine("  problém: " + p);
        }

        t.AppendLine("  výpadky prúdu skúšaš ručne: po každom musí PC naštartovať čistú vrstvu (alebo pôvodný Windows v bezpečnom režime).");
        t.AppendLine();
        t.AppendLine("POSLEDNÉ UDALOSTI");
        foreach (var e in journal.TakeLast(40))
        {
            var slot = e.Slot is { } s ? $" [{s}]" : "";
            var pct = e.Percent is { } p ? $" {p} %" : "";
            t.AppendLine($"  {e.Seq,5} {Format.Local(e.Utc)}{slot} {e.Kind}{pct}: {e.Text}");
        }

        return t.ToString();
    }

    public static string PhaseText(LabPhase phase) => phase switch
    {
        LabPhase.Preparing => "prevod prebieha alebo bol prerušený",
        LabPhase.FirstBootPending => "pripravené – čaká sa na prvý štart z vrstvy A",
        LabPhase.Running => "zmrazenie beží",
        LabPhase.Uninstalling => "odinštalovanie – dokonči ho v pôvodnom Windows",
        _ => phase.ToString(),
    };

    public static string ModeText(LabMode mode) => mode switch
    {
        LabMode.Safe => "Bezpečný (po výpadku alebo chybe naštartuje pôvodný Windows)",
        _ => "Ostrý (po výpadku alebo chybe naštartuje ďalšia čistá vrstva)",
    };

    private static void AppendStats(StringBuilder t, string label, IEnumerable<long> values)
    {
        var list = values.ToList();
        if (list.Count == 0)
        {
            return;
        }

        t.AppendLine($"{label}: priemer {Format.Seconds(list.Average() / 1000)}, najmenej {Format.Seconds(list.Min() / 1000.0)}, "
            + $"najviac {Format.Seconds(list.Max() / 1000.0)} ({list.Count}×)");
    }
}
