using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Polaris.Core;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>What the commands need from the world – the real one on a PC, a made-up one in tests.</summary>
public sealed class AgentHost
{
    /// <summary>The executors for a recipe in a folder (installers are found next to the recipe).</summary>
    public required Func<string, IReadOnlyList<IElementHandler>> Handlers { get; init; }

    public required Func<bool> IsAdmin { get; init; }

    public required string JournalPath { get; init; }

    public required Func<DateTimeOffset> Clock { get; init; }

    /// <summary>Asks the person whether to go on (true = yes).</summary>
    public required Func<string, bool> Confirm { get; init; }

    [SupportedOSPlatform("windows")]
    public static AgentHost Windows() => new()
    {
        Handlers = folder => [new RegistryHandler(), new ProgramHandler(folder), new ServiceHandler(), new FeatureHandler()],
        IsAdmin = () =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        },
        JournalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Product.Name, "agent", "dennik.jsonl"),
        Clock = () => DateTimeOffset.UtcNow,
        Confirm = question =>
        {
            Console.Write(question + " [a/n] ");
            return Console.ReadLine()?.Trim().ToLowerInvariant() is "a" or "ano" or "áno" or "y" or "yes";
        },
    };
}

/// <summary>The commands of polaris-agent. 0 = done or the PC is as the recipe wants, 1 = differences or failures,
/// 2 = wrong input, a recipe with problems or missing rights.</summary>
public static class Commands
{
    public const string Stage = "K1";

    public const string Help = """
        polaris-agent – uvedie toto PC do stavu podľa receptu (etapa K1: spúšťa správca na PC, bez siete)

          overit RECEPT          skontroluje recept (nič nemení, funguje kdekoľvek)
          odtlacok RECEPT        odtlačok SHA-256 receptu (na schválenie verzie profilu)
          kontrola RECEPT        čo sa na tomto PC líši od receptu (nič nemení)
          pouzi RECEPT [--ano]   urobí zmeny a každú overí (správca; bez --ano sa najprv opýta)
          dennik [POČET]         posledné behy na tomto PC (predvolene 10)
          verzia

        Recept je súbor JSON s prvkami: program (WinGet alebo inštalátor zo zásoby), registry (hodnota v HKLM),
        bookmarks (záložky pre Edge a Chrome), service (spúšťanie služby), feature (súčasť Windows, napr. Sandbox).
        Popis formátu: docs/recept.md v repozitári.
        """;

    public static int Run(string[] args, TextWriter output, AgentHost? host = null)
    {
        if (args.Length == 0 || args[0] is "pomoc" or "-h" or "--help" or "/?")
        {
            output.Write(Help);
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "verzia" => Version(output),
                "overit" => Validate(Need(args, "overit RECEPT"), output),
                "odtlacok" => Fingerprint(Need(args, "odtlacok RECEPT"), output),
                "kontrola" => Check(Need(args, "kontrola RECEPT"), output, host ?? DefaultHost()),
                "pouzi" => Apply(Need(args, "pouzi RECEPT [--ano]"), args.Contains("--ano"), output, host ?? DefaultHost()),
                "dennik" => Journal(args.Length > 1 && int.TryParse(args[1], out var n) && n > 0 ? n : 10, output, host ?? DefaultHost()),
                _ => Wrong($"Neznámy príkaz „{args[0]}“. Zoznam príkazov: polaris-agent pomoc", output),
            };
        }
        catch (InvalidDataException e)
        {
            return Wrong(e.Message, output);
        }
        catch (IOException e)
        {
            return Wrong("Súbor: " + e.Message, output);
        }
        catch (UnauthorizedAccessException e)
        {
            return Wrong("Prístup odmietnutý: " + e.Message, output);
        }
        catch (PlatformNotSupportedException)
        {
            return Wrong("Tento príkaz pracuje s PC, a preto beží len vo Windows. Na inom systéme funguje overit a odtlacok.", output);
        }
    }

    private static int Version(TextWriter output)
    {
        output.WriteLine($"{Product.Name} agent {Product.Version} ({Stage}), recept schéma {Recipe.CurrentSchema}");
        return 0;
    }

    private static int Validate(string file, TextWriter output)
    {
        var recipe = RecipeText.Load(file);
        var problems = RecipeCheck.Problems(recipe);
        if (problems.Count > 0)
        {
            output.WriteLine($"Recept {Path.GetFileName(file)} má problémy ({problems.Count}):");
            foreach (var p in problems)
            {
                output.WriteLine("  – " + p);
            }

            return 1;
        }

        var simple = Expansion.ExpandAll(recipe.Elements).Count();
        output.WriteLine($"Recept {recipe.Name} v{recipe.Version} je v poriadku: {recipe.Elements.Count} prvkov ({simple} po rozložení).");
        output.WriteLine($"Odtlačok: {RecipeText.Fingerprint(recipe)}");
        return 0;
    }

    private static int Fingerprint(string file, TextWriter output)
    {
        output.WriteLine(RecipeText.Fingerprint(RecipeText.Load(file)));
        return 0;
    }

    private static int Check(string file, TextWriter output, AgentHost host)
    {
        var (recipe, _, plan) = Prepare(file, output, host);
        if (plan is null)
        {
            return 2;
        }

        output.WriteLine(RunReport.PlanText(plan, recipe));
        return plan.InDesiredState ? 0 : 1;
    }

    private static int Apply(string file, bool yes, TextWriter output, AgentHost host)
    {
        if (!host.IsAdmin())
        {
            return Wrong("Zmeny na PC smie robiť len správca – spusti príkazový riadok ako správca.", output);
        }

        var (recipe, engine, plan) = Prepare(file, output, host);
        if (plan is null)
        {
            return 2;
        }

        output.WriteLine(RunReport.PlanText(plan, recipe));
        if (!plan.Changes.Any())
        {
            return plan.InDesiredState ? 0 : 1;
        }

        if (!yes && !host.Confirm($"Urobiť {plan.Changes.Count()} zmien?"))
        {
            output.WriteLine("Nič sa nezmenilo.");
            return 2;
        }

        var run = engine.Apply(plan, recipe, host.Clock, line => output.WriteLine(line));
        output.WriteLine(RunReport.RunText(run));
        WriteJournal(host.JournalPath, run);
        return run.Failed == 0 ? 0 : 1;
    }

    private static int Journal(int count, TextWriter output, AgentHost host)
    {
        if (!File.Exists(host.JournalPath))
        {
            output.WriteLine("Denník je prázdny – na tomto PC sa ešte žiadny recept nepoužil.");
            return 0;
        }

        foreach (var run in RunReport.ReadJournal(File.ReadAllText(host.JournalPath, Encoding.UTF8)).TakeLast(count))
        {
            var ok = run.Results.Count(r => r.Ok);
            var state = run.Failed == 0 ? "v poriadku" : $"chyby: {run.Failed}";
            output.WriteLine($"{run.StartedUtc.ToLocalTime():d. M. yyyy H:mm}  {run.Recipe} v{run.Version}  zmien {ok}, {state}");
        }

        return 0;
    }

    private static (Recipe Recipe, Engine Engine, Plan? Plan) Prepare(string file, TextWriter output, AgentHost host)
    {
        var recipe = RecipeText.Load(file);
        var engine = new Engine(host.Handlers(Folder(file)));
        var problems = RecipeCheck.Problems(recipe);
        if (problems.Count > 0)
        {
            output.WriteLine("Recept má problémy, na PC sa nepoužije (podrobnosti: polaris-agent overit).");
            foreach (var p in problems.Take(5))
            {
                output.WriteLine("  – " + p);
            }

            return (recipe, engine, null);
        }

        return (recipe, engine, engine.Plan(recipe.Elements));
    }

    private static void WriteJournal(string path, RunRecord run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(Encoding.UTF8.GetBytes(RunReport.ToLine(run) + "\n"));
    }

    private static string Folder(string file) => Path.GetDirectoryName(Path.GetFullPath(file))!;

    private static string Need(string[] args, string usage) =>
        args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1]
            : throw new InvalidDataException("Použitie: polaris-agent " + usage);

    private static AgentHost DefaultHost() =>
        OperatingSystem.IsWindows() ? AgentHost.Windows() : throw new PlatformNotSupportedException();

    private static int Wrong(string message, TextWriter output)
    {
        output.WriteLine("Chyba: " + message);
        return 2;
    }
}
