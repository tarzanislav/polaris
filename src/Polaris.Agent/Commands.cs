using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Polaris.Core;
using Polaris.Net;
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

    /// <summary>The consoles this PC trusts; when there is at least one, only recipes signed by them are applied.</summary>
    public required Func<IReadOnlyList<TrustedConsole>> Trusted { get; init; }

    public required Action<ConsoleKeyFile> AddTrusted { get; init; }

    public required Func<string, bool> RemoveTrusted { get; init; }

    /// <summary>One request to the School store of this PC (the agent's service answers).</summary>
    public Func<StoreRequest, StoreAnswer>? Store { get; init; }

    /// <summary>A line typed by the person (null = end of input).</summary>
    public Func<string?> ReadLine { get; init; } = Console.ReadLine;

    [SupportedOSPlatform("windows")]
    public static AgentHost Windows() => new()
    {
        Handlers = folder =>
        [
            new RegistryHandler(), new ProgramHandler(folder), new ServiceHandler(), new FeatureHandler(), new ComputerHandler(),
            new ProfileTemplateHandler(), new SessionRuleHandler(), new SandboxHandler(), new StoreHandler(),
        ],
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
        Trusted = TrustStore.Read,
        AddTrusted = TrustStore.Add,
        RemoveTrusted = TrustStore.Remove,
        Store = request => StoreClient.Ask(request, StoreClient.InstallLimit),
    };
}

/// <summary>The commands of polaris-agent. 0 = done or the PC is as the recipe wants, 1 = differences or failures,
/// 2 = wrong input, a recipe with problems or missing rights.</summary>
public static class Commands
{
    public const string Stage = "K2";

    public const string Help = """
        polaris-agent – uvedie toto PC do stavu podľa receptu (etapa K2: spúšťa správca na PC, bez siete)

          overit RECEPT          skontroluje recept (nič nemení, funguje kdekoľvek)
          odtlacok RECEPT        odtlačok SHA-256 receptu (na schválenie verzie profilu); --kanonicky vypíše text,
                                 z ktorého sa počíta (docs/kanonicky-tvar.md)
          kontrola RECEPT        čo sa na tomto PC líši od receptu (nič nemení)
          pouzi RECEPT [--ano]   urobí zmeny a každú overí (správca; bez --ano sa najprv opýta)
          dennik [POČET]         posledné behy na tomto PC (predvolene 10)
          doverovat KLUC         toto PC bude prijímať recepty podpísané touto konzolou (KONZOLA.verejny.json; správca)
          nedoverovat ODTLACOK   zruší dôveru ku konzole (správca)
          konzoly                konzoly, ktorým toto PC dôveruje
          instalovat-sluzbu      skopíruje agenta do Program Files a spustí službu POLARIS agent (čistí relácie; správca)
          odinstalovat-sluzbu    zastaví a odstráni službu (správca)
          vycistit KONTO         hneď zmaže profil zdieľaného konta, ak na ňom nikto nie je prihlásený (správca)
          centrum ADRESA ODTLAČOK SKUPINA   PC berie plán skupiny z centra (https://server:8443, odtlačok z centra; správca)
          centrum [teraz | -]    stav spojenia; teraz = stiahne plán a použije profil hneď; - = odpojí od centra
          zasoba [CESTA | -]     Zásoba balíkov tohto PC (napr. \\server\polaris-zasoba); bez cesty ju vypíše, - zruší
          obchod                 Školský obchod pre žiaka: zoznam programov a inštalácia na túto reláciu
          obchod zoznam | obchod instalovat ID

        Keď PC dôveruje aspoň jednej konzole, použije len recept s platným podpisom (RECEPT.podpis vedľa receptu).
          verzia

        Recept je súbor JSON s prvkami: program, registry, bookmarks, service, feature, session, keyboards,
        user-registry, sandbox (šablóna Pieskoviska) a store (Školský obchod).
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
                "odtlacok" => Fingerprint(Need(args, "odtlacok RECEPT [--kanonicky]"), args.Contains("--kanonicky"), output),
                "kontrola" => Check(Need(args, "kontrola RECEPT"), output, host ?? DefaultHost()),
                "pouzi" => Apply(Need(args, "pouzi RECEPT [--ano]"), args.Contains("--ano"), output, host ?? DefaultHost()),
                "doverovat" => AddTrust(Need(args, "doverovat KONZOLA.verejny.json"), output, host ?? DefaultHost()),
                "nedoverovat" => RemoveTrust(Need(args, "nedoverovat ODTLACOK"), output, host ?? DefaultHost()),
                "konzoly" => ListTrust(output, host ?? DefaultHost()),
                "instalovat-sluzbu" => Service(install: true, output, host ?? DefaultHost()),
                "odinstalovat-sluzbu" => Service(install: false, output, host ?? DefaultHost()),
                "vycistit" => CleanNow(Need(args, "vycistit KONTO"), output, host ?? DefaultHost()),
                "obchod" => Store(args, output, host ?? DefaultHost()),
                "zasoba" => Supply(args, output, host ?? DefaultHost()),
                "centrum" => CentreCommand(args, output, host ?? DefaultHost()),
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
        var signature = ReadSignature(file);
        output.WriteLine(signature is null
            ? "Podpis: žiadny."
            : RecipeSigning.Check(recipe, signature, []).State == SignatureState.ContentChanged
                ? "Podpis: NEPLATÍ – recept sa po podpísaní zmenil."
                : $"Podpis: konzola {signature.Console} ({signature.ConsoleKey}); dôveru overí PC pri použití.");
        return 0;
    }

    private static int Fingerprint(string file, bool canonical, TextWriter output)
    {
        var recipe = RecipeText.Load(file);
        output.WriteLine(canonical ? RecipeText.Canonical(recipe) : RecipeText.Fingerprint(recipe));
        return 0;
    }

    private static int Check(string file, TextWriter output, AgentHost host)
    {
        var (recipe, checkEngine, plan) = Prepare(file, output, host);
        using var _ = checkEngine;
        if (plan is null)
        {
            return 2;
        }

        output.WriteLine(RunReport.PlanText(plan, recipe));
        output.WriteLine("Podpis: " + RecipeSigning.Check(recipe, ReadSignature(file), host.Trusted()).Message + ".");
        return plan.InDesiredState ? 0 : 1;
    }

    private static int Apply(string file, bool yes, TextWriter output, AgentHost host)
    {
        if (!host.IsAdmin())
        {
            return Wrong("Zmeny na PC smie robiť len správca – spusti príkazový riadok ako správca.", output);
        }

        var (recipe, engine, plan) = Prepare(file, output, host);
        using var __ = engine;
        if (plan is null)
        {
            return 2;
        }

        var trusted = host.Trusted();
        var check = RecipeSigning.Check(recipe, ReadSignature(file), trusted);
        if (trusted.Count > 0 && check.State != SignatureState.Valid)
        {
            return Wrong($"Recept sa nepoužije: {check.Message}. Toto PC prijíma len recepty podpísané konzolami, ktorým dôveruje.", output);
        }

        output.WriteLine(trusted.Count == 0
            ? "Pozor: toto PC zatiaľ nedôveruje žiadnej konzole – recept sa použije bez overenia podpisu (nastav: polaris-agent doverovat)."
            : "Podpis: " + check.Message + ".");
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

    private static int AddTrust(string file, TextWriter output, AgentHost host)
    {
        if (!host.IsAdmin())
        {
            return Wrong("Dôveru ku konzole smie nastaviť len správca.", output);
        }

        var key = RecipeSigning.KeyFromJson(File.ReadAllText(file, Encoding.UTF8));
        if (key.EncryptedPrivateKey is not null)
        {
            return Wrong("Toto je SÚKROMNÝ kľúč konzoly – na PC patrí len verejný (.verejny.json). Súkromný kľúč z tohto PC zmaž.", output);
        }

        host.AddTrusted(key);
        output.WriteLine($"Toto PC dôveruje konzole {key.Name} (odtlačok {key.Fingerprint}). Odteraz prijme len recepty s platným podpisom.");
        return 0;
    }

    private static int RemoveTrust(string fingerprint, TextWriter output, AgentHost host)
    {
        if (!host.IsAdmin())
        {
            return Wrong("Dôveru ku konzole smie zrušiť len správca.", output);
        }

        if (!host.RemoveTrusted(fingerprint))
        {
            return Wrong($"Konzolu s odtlačkom {fingerprint} toto PC nepozná (zoznam: polaris-agent konzoly).", output);
        }

        output.WriteLine($"Dôvera ku konzole {fingerprint} zrušená.");
        return 0;
    }

    private static int ListTrust(TextWriter output, AgentHost host)
    {
        var list = host.Trusted();
        if (list.Count == 0)
        {
            output.WriteLine("Toto PC nedôveruje žiadnej konzole – recepty sa používajú bez overenia podpisu.");
        }

        foreach (var t in list)
        {
            output.WriteLine($"{t.Fingerprint}  {t.Name}");
        }

        return 0;
    }

    private static int Service(bool install, TextWriter output, AgentHost host)
    {
        if (!host.IsAdmin())
        {
            return Wrong("Službu smie inštalovať a odstraňovať len správca.", output);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (install)
        {
            AgentService.Install(Environment.ProcessPath ?? throw new InvalidOperationException("Cesta k programu sa nedá zistiť."), output);
        }
        else
        {
            AgentService.Uninstall(output);
        }

        return 0;
    }

    private static int CleanNow(string account, TextWriter output, AgentHost host)
    {
        if (!host.IsAdmin())
        {
            return Wrong("Profil konta smie zmazať len správca.", output);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var result = SessionCleaner.Clean(account, SessionCleaning.AtSignOut, DateTime.Now, force: true, AgentService.Store(host, new StoreState()).RemoveFor);
        output.WriteLine(char.ToUpperInvariant(result.Message[0]) + result.Message[1..] + ".");
        if (result.Cleaned)
        {
            WriteJournal(host.JournalPath, new RunRecord
            {
                StartedUtc = host.Clock(),
                FinishedUtc = host.Clock(),
                Recipe = "(relácia)",
                Results = [new ChangeResult(account, $"relácia konta {account}", ChangeKind.Remove, true, result.Message, 0)],
            });
        }

        return result.Cleaned || result.Message.Contains("čistý", StringComparison.Ordinal) ? 0 : 1;
    }

    private static int CentreCommand(string[] args, TextWriter output, AgentHost host)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (args.Length == 1)
        {
            if (Centre.Settings() is not { } s)
            {
                output.WriteLine("PC nie je pripojené k centru.");
                return 0;
            }

            var (profile, fingerprint, at) = Centre.Applied();
            output.WriteLine($"Centrum: {s.Address} (odtlačok {s.Pin[..16]}…), skupina {s.Group}, plán {Centre.Sequence()}.");
            output.WriteLine(profile is null ? "Profil z centra sa ešte nepoužil." : $"Naposledy použitý profil {profile} ({fingerprint?[..12]}…) {at?.ToLocalTime():d. M. H:mm}.");
            return 0;
        }

        if (!host.IsAdmin())
        {
            return Wrong("Spojenie s centrom smie meniť len správca.", output);
        }

        if (args[1] == "-")
        {
            Centre.Configure(null);
            output.WriteLine("PC odpojené od centra. Posledný profil ostáva, kým ho nezmení recept.");
            return 0;
        }

        CentreSettings settings;
        if (args[1] == "teraz")
        {
            settings = Centre.Settings() ?? throw new InvalidDataException("PC nie je pripojené k centru: polaris-agent centrum ADRESA ODTLAČOK SKUPINA");
        }
        else
        {
            if (args.Length != 4 || !Uri.TryCreate(args[1], UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
            {
                return Wrong("Použitie: polaris-agent centrum https://SERVER:8443 ODTLAČOK SKUPINA", output);
            }

            if (args[2].Length != 64 || !args[2].All(char.IsAsciiHexDigit))
            {
                return Wrong("Odtlačok centra má 64 znakov 0-9 a-f (vypíše ho polaris-centrum server certifikat).", output);
            }

            settings = new CentreSettings(address, args[2].ToLowerInvariant(), args[3]);
            Centre.Configure(settings);
            output.WriteLine($"PC pripojené k centru {address}, skupina {args[3]}.");
        }

        var sync = Centre.Sync(settings, host);
        var outcome = sync.Fetch(Centre.Sequence());
        if (outcome.Changed)
        {
            Centre.SetSequence(outcome.Sequence);
        }

        output.WriteLine("Plán: " + outcome.Message + ".");
        if (args[1] != "teraz")
        {
            return sync.Plan() is null ? 1 : 0;
        }

        var run = Centre.Tick(sync, host, DateTime.Now, force: true);
        if (run is null)
        {
            output.WriteLine("Nie je čo použiť – PC nemá platný plán.");
            return 1;
        }

        output.WriteLine(RunReport.RunText(run));
        WriteJournal(host.JournalPath, run);
        return run.Failed == 0 ? 0 : 1;
    }

    private static int Supply(string[] args, TextWriter output, AgentHost host)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (args.Length == 1)
        {
            output.WriteLine(Packages.Configured() is { } path
                ? $"Zásoba balíkov: {path}"
                : "Zásoba balíkov nie je nastavená – použije sa priečinok zasoba vedľa receptu, ak existuje.");
            return 0;
        }

        if (!host.IsAdmin())
        {
            return Wrong("Zásobu smie nastaviť len správca.", output);
        }

        if (args[1] == "-")
        {
            Packages.Configure(null);
            output.WriteLine("Zásoba balíkov zrušená.");
            return 0;
        }

        var root = args[1];
        if (root.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Centre.Settings() is null)
            {
                return Wrong("Zásoba na centre potrebuje spojenie s centrom: najprv polaris-agent centrum ADRESA ODTLAČOK SKUPINA.", output);
            }

            Packages.Configure(root);
            output.WriteLine($"Zásoba balíkov: {root} (cez centrum, s jeho odtlačkom).");
            return 0;
        }

        if (!Directory.Exists(Path.Combine(root, "baliky")))
        {
            return Wrong($"V {root} nie je zásoba (chýba priečinok baliky). Vytvára ju polaris-centrum zasoba pridat.", output);
        }

        Packages.Configure(root);
        output.WriteLine($"Zásoba balíkov: {root} ({new PackageStore(root).Ids().Count()} balíkov).");
        return 0;
    }

    /// <summary>The School store for the pupil: who they are, the service learns from Windows, not from this program.</summary>
    private static int Store(string[] args, TextWriter output, AgentHost host)
    {
        var ask = host.Store ?? throw new PlatformNotSupportedException();
        var sub = args.Length > 1 ? args[1] : null;
        if (sub is "instalovat")
        {
            return Install(Need(args[1..], "obchod instalovat ID"));
        }

        if (sub is not (null or "zoznam"))
        {
            return Wrong("Použitie: polaris-agent obchod [zoznam | instalovat ID]", output);
        }

        var list = ask(new StoreRequest { Command = "list" });
        if (!list.Ok)
        {
            output.WriteLine(list.Message);
            return 1;
        }

        output.WriteLine("Školský obchod – programy, ktoré si môžeš nainštalovať. Po skončení relácie sa samy odstránia.");
        for (var i = 0; i < list.Items.Count; i++)
        {
            var o = list.Items[i];
            var description = o.Description.Length > 0 ? " – " + o.Description : "";
            output.WriteLine($"  {i + 1,2}. {o.Title}{description}{(o.Installed ? "  [nainštalovaný]" : "")}   ({o.Id})");
        }

        if (sub is "zoznam")
        {
            return 0;
        }

        output.Write("Číslo programu na inštaláciu (Enter = koniec): ");
        var typed = host.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(typed))
        {
            return 0;
        }

        if (!int.TryParse(typed, out var n) || n < 1 || n > list.Items.Count)
        {
            return Wrong($"Zadaj číslo od 1 do {list.Items.Count}.", output);
        }

        return Install(list.Items[n - 1].Id);

        int Install(string id)
        {
            output.WriteLine("Inštalujem, môže to trvať niekoľko minút…");
            var answer = ask(new StoreRequest { Command = "install", Item = id });
            output.WriteLine(answer.Message);
            return answer.Ok ? 0 : 1;
        }
    }

    private static RecipeSignature? ReadSignature(string recipeFile)
    {
        var path = RecipeSigning.SignatureFileOf(recipeFile);
        return File.Exists(path) ? RecipeSigning.SignatureFromJson(File.ReadAllText(path, Encoding.UTF8)) : null;
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

    private static readonly Lock JournalGate = new();

    public static void WriteJournal(string path, RunRecord run)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        if (OperatingSystem.IsWindows())
        {
            AgentService.ProtectFolder(folder);
        }

        lock (JournalGate)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            stream.Write(Encoding.UTF8.GetBytes(RunReport.ToLine(run) + "\n"));
        }
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
