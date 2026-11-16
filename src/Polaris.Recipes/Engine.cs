using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Polaris.Recipes;

public enum Observed
{
    Absent,
    Present,

    /// <summary>The thing is there, but not as the recipe wants (another value, say).</summary>
    Different,
}

/// <summary>What an executor found on the PC, with a detail for people ("je 0, má byť 1").</summary>
public sealed record Observation(Observed State, string Detail = "");

/// <summary>The hands of the agent for one kind of element: read the real state, make the wanted state true.</summary>
public interface IElementHandler
{
    bool CanHandle(Element element);

    Observation Observe(Element element);

    /// <summary>Makes the element true as it is written (present or removed). A failure throws with a Slovak message.</summary>
    void Apply(Element element);
}

public enum ChangeKind
{
    None,
    Install,
    Change,
    Remove,

    /// <summary>The state cannot be found out or the agent cannot handle this element.</summary>
    Cannot,
}

public sealed record PlanItem(Element Element, ChangeKind Kind, string Before, IElementHandler? Handler);

/// <summary>What would change on the PC. Making a plan changes nothing.</summary>
public sealed class Plan(IReadOnlyList<PlanItem> items)
{
    public IReadOnlyList<PlanItem> Items { get; } = items;

    public bool InDesiredState => Items.All(i => i.Kind == ChangeKind.None);

    public IEnumerable<PlanItem> Changes => Items.Where(i => i.Kind is ChangeKind.Install or ChangeKind.Change or ChangeKind.Remove);
}

public sealed record ChangeResult(string ElementId, string Title, ChangeKind Kind, bool Ok, string Message, double Seconds);

/// <summary>The record of one run of a recipe on a PC – also one line of the agent's journal.</summary>
public sealed class RunRecord
{
    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset FinishedUtc { get; set; }

    public string Recipe { get; set; } = "";

    public int Version { get; set; }

    public string Fingerprint { get; set; } = "";

    public List<ChangeResult> Results { get; set; } = [];

    /// <summary>Elements that could not be handled at all (no executor, state unknown).</summary>
    public List<string> Skipped { get; set; } = [];

    [JsonIgnore]
    public int Failed => Results.Count(r => !r.Ok) + Skipped.Count;
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunRecord))]
internal sealed partial class RunJson : JsonSerializerContext;

/// <summary>Compares the PC with the wanted elements and makes the differences true. Every change is checked by reading
/// the state again; a failure of one element never stops the others.</summary>
public sealed class Engine(IReadOnlyList<IElementHandler> handlers)
{
    public Plan Plan(IEnumerable<Element> wanted)
    {
        var items = new List<PlanItem>();
        foreach (var element in Expansion.ExpandAll(wanted))
        {
            var handler = handlers.FirstOrDefault(h => h.CanHandle(element));
            if (handler is null)
            {
                items.Add(new PlanItem(element, ChangeKind.Cannot, "agent tento typ prvku zatiaľ nevie spracovať", null));
                continue;
            }

            Observation seen;
            try
            {
                seen = handler.Observe(element);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                items.Add(new PlanItem(element, ChangeKind.Cannot, "stav sa nedá zistiť: " + e.Message, handler));
                continue;
            }

            var kind = (element.Present, seen.State) switch
            {
                (true, Observed.Present) => ChangeKind.None,
                (true, Observed.Absent) => ChangeKind.Install,
                (true, Observed.Different) => ChangeKind.Change,
                (false, Observed.Absent) => ChangeKind.None,
                _ => ChangeKind.Remove,
            };
            items.Add(new PlanItem(element, kind, seen.Detail, handler));
        }

        return new Plan(items);
    }

    /// <summary>Removals first (from the last to the first), then installations and changes in the order of the recipe.</summary>
    public RunRecord Apply(Plan plan, Recipe? recipe, Func<DateTimeOffset> clock, Action<string>? progress = null)
    {
        var record = new RunRecord
        {
            StartedUtc = clock(),
            Recipe = recipe?.Name ?? "",
            Version = recipe?.Version ?? 0,
            Fingerprint = recipe is null ? "" : RecipeText.Fingerprint(recipe),
        };
        record.Skipped.AddRange(plan.Items.Where(i => i.Kind == ChangeKind.Cannot).Select(i => $"{i.Element.Id}: {i.Before}"));

        var order = plan.Items.Where(i => i.Kind == ChangeKind.Remove).Reverse()
            .Concat(plan.Items.Where(i => i.Kind is ChangeKind.Install or ChangeKind.Change));
        foreach (var item in order)
        {
            progress?.Invoke($"{RunReport.Verb(item.Kind)}: {item.Element.Title} …");
            var clockRun = Stopwatch.StartNew();
            string message;
            var ok = false;
            try
            {
                item.Handler!.Apply(item.Element);
                var after = item.Handler.Observe(item.Element);
                ok = item.Element.Present ? after.State == Observed.Present : after.State == Observed.Absent;
                message = ok ? RunReport.Done(item.Kind) : "zmena sa neprejavila – stav po nej: " + RunReport.State(after);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                message = e.Message;
            }

            record.Results.Add(new ChangeResult(item.Element.Id, item.Element.Title, item.Kind, ok, message, Math.Round(clockRun.Elapsed.TotalSeconds, 1)));
        }

        record.FinishedUtc = clock();
        return record;
    }
}

/// <summary>Plans and results in Slovak for people, and as a line for the journal.</summary>
public static class RunReport
{
    public static string Verb(ChangeKind kind) => kind switch
    {
        ChangeKind.Install => "pridať",
        ChangeKind.Change => "zmeniť",
        ChangeKind.Remove => "odstrániť",
        ChangeKind.None => "v poriadku",
        _ => "nedá sa",
    };

    public static string Done(ChangeKind kind) => kind switch
    {
        ChangeKind.Install => "pridané a overené",
        ChangeKind.Change => "zmenené a overené",
        ChangeKind.Remove => "odstránené a overené",
        _ => "bez zmeny",
    };

    public static string State(Observation o) => o.State switch
    {
        Observed.Present => "je tam",
        Observed.Absent => "nie je tam",
        _ => "je iné",
    } + (o.Detail.Length > 0 ? $" ({o.Detail})" : "");

    public static string PlanText(Plan plan, Recipe? recipe)
    {
        var sb = new StringBuilder();
        if (recipe is not null)
        {
            sb.AppendLine($"Recept {recipe.Name} v{recipe.Version} · odtlačok {RecipeText.Fingerprint(recipe)[..12]}…");
        }

        foreach (var item in plan.Items.Where(i => i.Kind != ChangeKind.None))
        {
            var mark = item.Kind switch
            {
                ChangeKind.Install => "+",
                ChangeKind.Change => "~",
                ChangeKind.Remove => "-",
                _ => "?",
            };
            var detail = item.Before.Length > 0 ? $" – {item.Before}" : "";
            sb.AppendLine($"  {mark} {Verb(item.Kind)}: {item.Element.Title}{detail}");
        }

        var fine = plan.Items.Count(i => i.Kind == ChangeKind.None);
        var changes = plan.Changes.Count();
        var cannot = plan.Items.Count(i => i.Kind == ChangeKind.Cannot);
        sb.Append(plan.InDesiredState
            ? $"PC je presne podľa receptu ({fine} prvkov v poriadku)."
            : $"Zmien: {changes}, v poriadku: {fine}, nedá sa spracovať: {cannot}.");
        return sb.ToString();
    }

    public static string RunText(RunRecord run)
    {
        var sb = new StringBuilder();
        foreach (var r in run.Results)
        {
            sb.AppendLine($"  {(r.Ok ? "ok   " : "CHYBA")} {r.Title}: {r.Message} ({r.Seconds:0.0} s)");
        }

        foreach (var s in run.Skipped)
        {
            sb.AppendLine($"  ?     {s}");
        }

        var ok = run.Results.Count(r => r.Ok);
        sb.Append(run.Failed == 0
            ? $"Hotovo: {ok} zmien, všetky overené."
            : $"Hotovo s chybami: {ok} zmien v poriadku, {run.Failed} sa nepodarilo.");
        return sb.ToString();
    }

    public static string ToLine(RunRecord run) => JsonSerializer.Serialize(run, RunJson.Default.RunRecord);

    public static RunRecord FromLine(string line) =>
        JsonSerializer.Deserialize(line, RunJson.Default.RunRecord) ?? throw new InvalidDataException("Prázdny záznam.");

    /// <summary>Reads the journal; a line cut by a power loss is skipped.</summary>
    public static List<RunRecord> ReadJournal(string text)
    {
        var list = new List<RunRecord>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                list.Add(FromLine(line));
            }
            catch (JsonException)
            {
                // a line written only partly
            }
        }

        return list;
    }
}
