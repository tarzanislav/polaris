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
            new ProfileTemplateHandler(), new SessionRuleHandler(), new SandboxHandler(), new StoreHandler(), new StartupHandler(),
            new PasswordPolicyHandler(), new AccountHandler(), new AutoLogonHandler(), new FolderHandler(), new UserPolicyHandler(),
            new StartLayoutHandler(),
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
    public const string Stage = "K6b";

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
          sluzby                 služby, ktoré POLARIS zmenil, a ich pôvodné spúšťanie
          sluzby vratit MENO | --vsetky
                                 vráti pôvodné spúšťanie služby (správca); recept ju však zmení znova, kým ju obsahuje
          ovladace [--vsetky]    zariadenia bez ovládača (--vsetky: všetky zariadenia s ich ovládačmi)
          ovladace priecinok [CESTA | --zrusit]
                                 priečinok záloh ovládačov: USB kľúč alebo zdieľaný priečinok, nikdy disk PC (správca);
                                 bez nastavenia POLARIS-ovladace na pripojenom USB kľúči
          ovladace zaloha [PRIEČINOK] [--meno ZOSTAVA] [--vsetky]
                                 záloha ovládačov tejto hardvérovej zostavy (správca) do podpriečinka „Výrobca Model -
                                 Windows 11 Pro“ v priečinku záloh; bez --vsetky len ovládače, ktoré zariadenia PC používajú
          ovladace overit [PRIEČINOK] skontroluje zálohy: každý súbor podľa odtlačku (nič nemení)
          ovladace obnov [PRIEČINOK] [--vsetky] [--ano]
                                 nainštaluje ovládače, ktoré toto PC potrebuje, zo zálohy jeho zostavy (správca; len
                                 podpísané); záloha sa nájde sama v priečinku záloh
          nespat [3h | 90m | koniec]
                                 údržba: PC nezaspí daný čas (najviac 24h), aby sa dalo pracovať na diaľku (správca);
                                 bez času vypíše stav
          spustanie              programy, ktoré sa spúšťajú samy po prihlásení (pre všetkých), a ktoré vypol POLARIS
          spustanie vratit MENO | --vsetky
                                 zapne znova program, ktorý vypol POLARIS (správca); recept ho však vypne znova
          meranie [POČET]        posledné merania prihlásenia: čas do pracovnej plochy a pamäť po 2 minútach
          trezor                 trezory profilov tohto PC (pripravené profily zdieľaných kont)
          trezor ulozit KONTO    uloží profil konta, ako je teraz, do trezoru (správca; nikto nesmie byť prihlásený) –
                                 s pravidlom relácie "Restore" sa po každom odhlásení vráti presne tak
          trezor priprav KONTO   príprava: profil konta sa nečistí, kým ho neuložíš (správca) – prihlás sa doň,
                                 spusti programy, nastav, čo treba, odhlás sa a daj trezor ulozit
          trezor zmazat KONTO    zmaže trezor konta (správca); relácia potom maže profil ako bez trezoru

        Keď PC dôveruje aspoň jednej konzole, použije len recept s platným podpisom (RECEPT.podpis vedľa receptu).
          verzia

        Recept je súbor JSON s prvkami: program, registry, bookmarks, service, feature, session, keyboards,
        user-registry, sandbox (šablóna Pieskoviska), store (Školský obchod), computer, updates, power, sounds
        startup, account a folder.
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
                "ovladace" => DriverCommand(args, output, host ?? DefaultHost()),
                "sluzby" => OperatingSystem.IsWindows() ? ServicesCommand(args, output, host ?? DefaultHost()) : throw new PlatformNotSupportedException(),
                "nespat" => OperatingSystem.IsWindows() ? AwakeCommand(args, output, host ?? DefaultHost()) : throw new PlatformNotSupportedException(),
                "spustanie" => OperatingSystem.IsWindows() ? StartupCommand(args, output, host ?? DefaultHost()) : throw new PlatformNotSupportedException(),
                "trezor" => OperatingSystem.IsWindows() ? VaultCommand(args, output, host ?? DefaultHost()) : throw new PlatformNotSupportedException(),
                "meranie" => Measurements(args.Length > 1 && int.TryParse(args[1], out var m) && m > 0 ? m : 5, output, host ?? DefaultHost()),
                "skuska-trezoru" => OperatingSystem.IsWindows() ? VaultCheck(output) : throw new PlatformNotSupportedException(),
                "skuska-ikony" => OperatingSystem.IsWindows() ? IconCheck(output, host ?? DefaultHost()) : throw new PlatformNotSupportedException(),
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
        catch (Polaris.Windows.SystemCallException e)
        {
            return Wrong(e.Message, output);
        }
        catch (SystemCallException e)
        {
            return Wrong(e.Message, output);
        }
        catch (InvalidOperationException e)
        {
            return Wrong(e.Message, output);
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

        RunRecord run;
        using (Awake.While("používa recept " + recipe.Name))
        {
            run = engine.Apply(plan, recipe, host.Clock, line => output.WriteLine(line));
        }

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
            if (run.Recipe == Measurement.Recipe)
            {
                output.WriteLine($"{run.StartedUtc.ToLocalTime():d. M. yyyy H:mm}  {run.Recipe}  {string.Join(", ", run.Results.Take(2).Select(r => r.Message))}");
                continue;
            }

            if (run.Recipe.StartsWith('(') && run.Results.Count == 1 && run.Results[0].Ok)
            {
                output.WriteLine($"{run.StartedUtc.ToLocalTime():d. M. yyyy H:mm}  {run.Recipe}  {run.Results[0].Message}");
                continue;
            }

            var ok = run.Results.Count(r => r.Ok);
            var state = run.Failed == 0 ? "v poriadku" : $"chyby: {run.Failed}";
            output.WriteLine($"{run.StartedUtc.ToLocalTime():d. M. yyyy H:mm}  {run.Recipe} v{run.Version}  zmien {ok}, {state}");
            foreach (var failed in run.Results.Where(r => !r.Ok))
            {
                output.WriteLine($"      ✗ {failed.Title}: {failed.Message}");
            }
        }

        return 0;
    }

    private static int Measurements(int count, TextWriter output, AgentHost host)
    {
        var runs = File.Exists(host.JournalPath)
            ? RunReport.ReadJournal(File.ReadAllText(host.JournalPath, Encoding.UTF8)).Where(r => r.Recipe == Measurement.Recipe).TakeLast(count).ToList()
            : [];
        if (runs.Count == 0)
        {
            output.WriteLine("Zatiaľ žiadne meranie – meria služba POLARIS agent pri každom prihlásení (ikona dá vedieť, kedy je plocha hotová).");
            return 0;
        }

        foreach (var run in runs)
        {
            output.WriteLine($"{run.StartedUtc.ToLocalTime():d. M. yyyy H:mm}");
            foreach (var r in run.Results)
            {
                output.WriteLine($"  {r.Title,-36} {r.Message}");
            }
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

    [SupportedOSPlatform("windows")]
    private static int ServicesCommand(string[] args, TextWriter output, AgentHost host)
    {
        var memory = new ServiceMemory();
        if (args.Length > 1 && args[1] == "vratit")
        {
            if (!host.IsAdmin())
            {
                return Wrong("Služby vracia správca.", output);
            }

            var names = args.Contains("--vsetky") ? [.. memory.All().Keys] : args.Skip(2).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (names.Count == 0)
            {
                return Wrong("Použitie: polaris-agent sluzby vratit MENO | --vsetky", output);
            }

            var original = memory.All();
            foreach (var name in names)
            {
                output.WriteLine(memory.Restore(name)
                    ? $"  ✓ {name}: späť na {original[name]}"
                    : $"  – {name}: POLARIS ju nemenil, nie je čo vrátiť");
            }

            output.WriteLine("Ak je služba v recepte, pri ďalšom použití receptu sa zmení znova – uprav recept.");
            return 0;
        }

        var all = memory.All();
        if (all.Count == 0)
        {
            output.WriteLine("POLARIS zatiaľ nezmenil žiadnu službu.");
            return 0;
        }

        output.WriteLine($"Služby, ktoré POLARIS zmenil ({all.Count}) – pôvodné spúšťanie:");
        foreach (var (name, start) in all)
        {
            output.WriteLine($"  {name,-32} pôvodne {start}");
        }

        output.WriteLine("Vrátiť: polaris-agent sluzby vratit MENO | --vsetky");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int VaultCheck(TextWriter output)
    {
        output.WriteLine("Skúška trezoru profilu (skúšobné konto, na konci zmazané):");
        var results = VaultSelfTest.Run();
        foreach (var (ok, what) in results)
        {
            output.WriteLine($"  {(ok ? "✓" : "✗")} {what}");
        }

        return results.All(r => r.Ok) ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    private static int VaultCommand(string[] args, TextWriter output, AgentHost host)
    {
        var vault = new ProfileVault();
        var action = args.Length > 1 ? args[1] : "";
        if (action.Length == 0)
        {
            var rules = SessionRules.Read();
            var accounts = Directory.Exists(vault.Folder) ? Directory.GetDirectories(vault.Folder).Select(Path.GetFileName).OfType<string>().ToList() : [];
            foreach (var account in rules.Where(r => r.Value == SessionCleaning.Restore).Select(r => r.Key))
            {
                if (!accounts.Contains(account, StringComparer.OrdinalIgnoreCase))
                {
                    accounts.Add(account);
                }
            }

            if (accounts.Count == 0)
            {
                output.WriteLine("Na tomto PC nie je žiadny trezor profilu. Uloženie: polaris-agent trezor ulozit KONTO");
                return 0;
            }

            foreach (var account in accounts.Order(StringComparer.OrdinalIgnoreCase))
            {
                var m = vault.Manifest(account);
                var sid = Profiles.SidOf(account);
                var rule = rules.TryGetValue(account, out var r) ? SessionRuleHandler.Describe(r) : "bez pravidla relácie";
                if (m is null)
                {
                    output.WriteLine($"  {account}: trezor nie je ({rule})");
                    continue;
                }

                var unfit = sid is null ? "konto neexistuje" : vault.Unfit(account, sid);
                var size = Measurement.Size(m.ArchiveLength);
                var windows = m.Windows == ProfileVault.WindowsBuild() ? "" : $", z inej zostavy Windows ({m.Windows}) – odporúčam uložiť znova";
                output.WriteLine($"  {account}: uložený {m.SavedUtc.ToLocalTime():d. M. yyyy H:mm}, {m.Entries.Count} položiek, {size}{windows}; {rule}"
                    + (unfit is null ? "" : $" – NEPLATÍ: {unfit}"));
            }

            return 0;
        }

        if (!host.IsAdmin())
        {
            return Wrong("Trezor profilu spravuje správca.", output);
        }

        var name = args.Length > 2 ? args[2].Trim() : throw new InvalidDataException($"Použitie: polaris-agent trezor {action} KONTO");
        switch (action)
        {
            case "ulozit":
                {
                    output.WriteLine($"Ukladám profil konta {name} do trezoru…");
                    var saved = vault.Save(name, host.Clock);
                    var message = $"trezor konta {name} uložený: {saved.Entries} položiek, {Measurement.Size(saved.Bytes)}, {Polaris.Core.Format.Seconds(saved.Seconds)}";
                    WriteJournal(host.JournalPath, AgentService.Simple("(trezor)", "trezor", "trezor profilu", true, message, host.Clock()));
                    output.WriteLine(char.ToUpperInvariant(message[0]) + message[1..] + ".");
                    output.WriteLine(SessionRules.Read().TryGetValue(name, out var rule) && rule == SessionCleaning.Restore
                        ? "Po každom odhlásení sa profil vráti presne do tohto stavu."
                        : "Aby sa profil po odhlásení vracal z trezoru, daj konto do receptu: \"session\" s \"clean\": \"Restore\".");
                    if (new StartLayoutHandler(vault).AfterSave(name) is { } start)
                    {
                        output.WriteLine(start);
                    }

                    return 0;
                }

            case "priprav":
                if (Profiles.SidOf(name) is null)
                {
                    return Wrong($"Konto {name} neexistuje.", output);
                }

                vault.SetPreparing(name, true);
                WriteJournal(host.JournalPath, AgentService.Simple("(trezor)", "trezor", "trezor profilu", true, $"príprava trezoru konta {name}", host.Clock()));
                output.WriteLine($"Príprava trezoru konta {name}: profil sa nečistí, kým ho neuložíš.");
                output.WriteLine($"Prihlás sa ako {name}, spusti programy, prejdi ich úvodné okná, nastav, čo treba, a odhlás sa. Potom: polaris-agent trezor ulozit {name}");
                return 0;

            case "zmazat":
                vault.Forget(name);
                WriteJournal(host.JournalPath, AgentService.Simple("(trezor)", "trezor", "trezor profilu", true, $"trezor konta {name} zmazaný", host.Clock()));
                output.WriteLine($"Trezor konta {name} zmazaný.");
                return 0;

            default:
                return Wrong($"Neznámy príkaz trezor {action} (možné: priprav, ulozit, zmazat).", output);
        }
    }

    [SupportedOSPlatform("windows")]
    private static int AwakeCommand(string[] args, TextWriter output, AgentHost host)
    {
        var now = host.Clock();
        if (args.Length == 1)
        {
            output.WriteLine(MaintenanceStore.Until() is { } u && u > now
                ? $"Údržba: PC nezaspí do {u.ToLocalTime():d. M. H:mm}. Skončiť: polaris-agent nespat koniec"
                : "Údržba nie je zapnutá – PC zaspáva podľa nastavenia napájania. Zapnúť: polaris-agent nespat 3h");
            return 0;
        }

        if (!host.IsAdmin())
        {
            return Wrong("Údržbu zapína správca.", output);
        }

        string message;
        if (args[1] is "koniec" or "-")
        {
            MaintenanceStore.Set(null);
            message = "údržba ukončená – PC zaspáva podľa nastavenia napájania";
        }
        else if (Maintenance.ParseLength(args[1]) is { } length)
        {
            var until = now + length;
            MaintenanceStore.Set(until);
            message = $"údržba: PC nezaspí do {until.ToLocalTime():d. M. H:mm}";
        }
        else
        {
            return Wrong("Použitie: polaris-agent nespat 3h | 90m | 1h30m | koniec (najviac 24h).", output);
        }

        WriteJournal(host.JournalPath, AgentService.Simple("(údržba)", "udrzba", "údržba", true, message, now));
        output.WriteLine(char.ToUpperInvariant(message[0]) + message[1..] + ".");
        var running = ServiceManager.Exists(AgentService.Name) && ServiceManager.Read(AgentService.Name).Current == ServiceManager.Running;
        output.WriteLine(running
            ? "Službe POLARIS agent to stačí do pol minúty (powercfg /requests ukáže jej požiadavku)."
            : "Pozor: služba POLARIS agent nebeží – údržba začne, až keď sa spustí (instalovat-sluzbu).");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int StartupCommand(string[] args, TextWriter output, AgentHost host)
    {
        var memory = new StartupMemory();
        if (args.Length > 1 && args[1] == "vratit")
        {
            if (!host.IsAdmin())
            {
                return Wrong("Programy zapína znova správca.", output);
            }

            var ids = args.Contains("--vsetky") ? [.. memory.All().Keys] : args.Skip(2).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (ids.Count == 0)
            {
                return Wrong("Použitie: polaris-agent spustanie vratit MENO | --vsetky", output);
            }

            foreach (var id in ids)
            {
                output.WriteLine(memory.Restore(id) ? $"  ✓ {id}: zase sa spúšťa" : $"  – {id}: POLARIS ho nevypol, nie je čo vrátiť");
            }

            output.WriteLine(StartupRule.Read() is null
                ? "Hotovo."
                : "Služba stráži zoznam povolených z receptu – program pridaj do „allow“ prvku startup, inak ho o chvíľu vypne znova.");
            return 0;
        }

        var held = memory.All();
        var entries = StartupEntries.Read();
        output.WriteLine($"Programy, ktoré sa spúšťajú samy po prihlásení pre všetkých ({entries.Count}):");
        foreach (var e in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine("  " + StartupWork.Describe(e) + (held.ContainsKey(e.Id) ? "  – vypol POLARIS" : ""));
        }

        var allow = StartupRule.Read();
        output.WriteLine(allow is null
            ? "Služba nestráži žiadny zoznam povolených (recept nemá prvok startup)."
            : $"Povolené receptom: {(allow.Count == 0 ? "nič" : string.Join(", ", allow))} (a vždy SecurityHealth).");
        output.WriteLine("Programy jednotlivých kont (ich vlastné „Run“) rieši šablóna profilu.");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int IconCheck(TextWriter output, AgentHost host)
    {
        if (!host.IsAdmin())
        {
            return Wrong("Skúšku ikony spúšťa správca (ikona sa spúšťa ako žiak – jeho konto bez práv správcu).", output);
        }

        var program = Path.Combine(AppContext.BaseDirectory, IconLauncher.ProgramName);
        if (!File.Exists(program))
        {
            return Wrong($"Vedľa agenta chýba {IconLauncher.ProgramName}.", output);
        }

        output.WriteLine($"Ikona ako žiak (bez práv správcu), každá možnosť {IconSelfTest.Wait.TotalSeconds:0} s:");
        var results = IconSelfTest.Run(program, output);
        return results[0].Alive ? 0 : 1;
    }

    private static int DriverCommand(string[] args, TextWriter output, AgentHost host)
    {
        var all = args.Contains("--vsetky");
        var action = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "";
        if (action == "overit")
        {
            var folder = Positional(args, 2)
                ?? (OperatingSystem.IsWindows() ? DriverPlace.Root()?.Folder : null)
                ?? throw new InvalidDataException("Použitie: polaris-agent ovladace overit PRIEČINOK (záloha alebo priečinok záloh)");
            var found = OperatingSystem.IsWindows()
                ? DriverPlace.Find(folder, output)
                : [new FoundBackup(folder, DriverBackupFormat.Load(folder))];
            if (found.Count == 0)
            {
                return Wrong($"V {folder} nie je žiadna záloha ovládačov ({DriverBackup.FileName}).", output);
            }

            var bad = 0;
            foreach (var (place, backup) in found)
            {
                var problems = DriverBackupFormat.VerifyFiles(place, backup);
                if (found.Count > 1)
                {
                    output.WriteLine(Path.GetFileName(place) + ":");
                }

                output.WriteLine($"Záloha ovládačov {backup.Name}: {backup.Packages.Count} balíkov, {backup.Packages.Sum(p => p.Files.Count)} súborov, z Windows {backup.Windows}.");
                output.WriteLine("Odtlačok: " + DriverBackupFormat.Fingerprint(backup));
                foreach (var problem in problems)
                {
                    output.WriteLine("  – " + problem);
                }

                output.WriteLine(problems.Count == 0 ? "Všetky súbory sedia." : $"Záloha má problémy ({problems.Count}) – obnova by ju odmietla.");
                bad += problems.Count == 0 ? 0 : 1;
            }

            return bad == 0 ? 0 : 1;
        }

        return OperatingSystem.IsWindows() ? WindowsDrivers(action, args, all, output, host) : throw new PlatformNotSupportedException();
    }

    [SupportedOSPlatform("windows")]
    private static int WindowsDrivers(string action, string[] args, bool all, TextWriter output, AgentHost host)
    {
        switch (action)
        {
            case "":
                var devices = Polaris.Windows.Devices.Present();
                var missing = devices.Where(d => d.MissingDriver).ToList();
                foreach (var d in all ? devices.OrderBy(d => d.Class, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase) : missing.AsEnumerable())
                {
                    output.WriteLine("  " + (d.HasProblem ? $"! (kód {d.Problem}) " : "") + DriverWork.Describe(d));
                    if (d.MissingDriver)
                    {
                        output.WriteLine("      " + string.Join(", ", d.HardwareIds.Take(2)));
                    }
                }

                output.WriteLine(missing.Count == 0
                    ? $"Všetkých {devices.Count} zariadení má ovládač."
                    : $"Bez ovládača: {missing.Count} z {devices.Count} zariadení. Doplniť sa dajú zo zálohy zostavy (ovladace obnov).");
                return missing.Count == 0 ? 0 : 1;

            case "priecinok":
                {
                    var given = Positional(args, 2);
                    if (given is null && !args.Contains("--zrusit"))
                    {
                        var root = DriverPlace.Root();
                        output.WriteLine(root is { } r
                            ? $"Priečinok záloh ovládačov: {r.Folder} ({r.How})."
                            : "Priečinok záloh ovládačov nie je nastavený a USB kľúč nie je pripojený.");
                        output.WriteLine("Nastaviť: polaris-agent ovladace priecinok E:\\Ovladace alebo \\\\server\\zdielanie\\Ovladace (USB kľúč, zdieľaný priečinok).");
                        output.WriteLine("Táto zostava: " + ThisBuild().Name);
                        return 0;
                    }

                    if (!host.IsAdmin())
                    {
                        return Wrong("Priečinok záloh nastavuje správca.", output);
                    }

                    if (given is null)
                    {
                        DriverPlace.Set(null);
                        output.WriteLine($"Nastavenie zrušené: zálohy pôjdu do {DriverBuilds.DefaultFolderOnStick} na pripojenom USB kľúči.");
                        return 0;
                    }

                    var full = Path.GetFullPath(given);
                    if (DriverPlace.OnThisPc(full) is { } where && !args.Contains("--aj-na-tomto-pc"))
                    {
                        return Wrong($"{full} je {where} – zálohy ovládačov sú pre iné PC a na disku PC by ich nová inštalácia zmazala. Daj USB kľúč alebo zdieľaný priečinok (externý USB disk, ktorý sa hlási ako pevný: --aj-na-tomto-pc).", output);
                    }

                    DriverPlace.Set(full);
                    output.WriteLine($"Priečinok záloh ovládačov: {full}. Záloha: polaris-agent ovladace zaloha (podpriečinok podľa zostavy vytvorí sám).");
                    return 0;
                }

            case "zaloha":
                {
                    if (!host.IsAdmin())
                    {
                        return Wrong("Zálohu ovládačov robí správca.", output);
                    }

                    var build = ThisBuild();
                    var at = Array.IndexOf(args, "--meno");
                    var name = at > 0 && at + 1 < args.Length ? args[at + 1].Trim() : build.Name;
                    if (name.Length == 0)
                    {
                        return Wrong("Meno zostavy po --meno chýba.", output);
                    }

                    string folder;
                    if (Positional(args, 2) is { } given)
                    {
                        folder = Path.GetFullPath(given);
                    }
                    else if (DriverPlace.Root() is { } root)
                    {
                        output.WriteLine($"Priečinok záloh: {root.Folder} ({root.How}).");
                        folder = DriverBuilds.NewFolder(root.Folder, name, DateTime.Now, path => Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any());
                    }
                    else
                    {
                        return Wrong("Kam zálohovať? Pripoj USB kľúč (záloha pôjde do " + DriverBuilds.DefaultFolderOnStick + " na ňom) alebo nastav priečinok: polaris-agent ovladace priecinok \\\\server\\zdielanie\\Ovladace", output);
                    }

                    if (DriverPlace.OnThisPc(folder) is { } where && !args.Contains("--aj-na-tomto-pc"))
                    {
                        return Wrong($"{folder} je {where} – ovládače sa na disk PC nekopírujú (nová inštalácia by ich zmazala). Pripoj USB kľúč alebo nastav priečinok záloh (ovladace priecinok); výnimočne --aj-na-tomto-pc.", output);
                    }

                    output.WriteLine($"Záloha ovládačov zostavy {name} do {folder}:");
                    var backup = DriverWork.Backup(folder, name, all, output, build.Model ?? "", build.Edition);
                    var unsigned = backup.Packages.Count(p => p.Signer.Length == 0);
                    output.WriteLine($"Hotovo: {backup.Packages.Count} balíkov, {backup.Packages.Sum(p => p.Files.Count)} súborov.{(unsigned > 0 ? $" Bez platného podpisu: {unsigned} (obnova ich odmietne)." : "")}");
                    output.WriteLine("Odtlačok zálohy: " + DriverBackupFormat.Fingerprint(backup));
                    return 0;
                }

            case "obnov":
                {
                    if (!host.IsAdmin())
                    {
                        return Wrong("Ovládače inštaluje správca.", output);
                    }

                    var chosen = ChooseBackup(Positional(args, 2), output);
                    if (chosen is null)
                    {
                        return 1;
                    }

                    var folder = chosen.Folder;
                    var backup = chosen.Backup;
                    var problems = DriverBackupFormat.Problems(backup);
                    if (problems.Count > 0)
                    {
                        return Wrong("Záloha má problémy: " + string.Join(" ", problems), output);
                    }

                    var fingerprint = DriverBackupFormat.Fingerprint(backup);
                    var needed = DriverWork.Needed(backup, Polaris.Windows.Devices.Present(), all);
                    output.WriteLine($"Záloha {backup.Name} ({backup.Packages.Count} balíkov), odtlačok {fingerprint}.");
                    if (needed.Count == 0)
                    {
                        output.WriteLine("Žiadny balík zo zálohy nepatrí k zariadeniam tohto PC. (--vsetky by nainštaloval všetky.)");
                        return 0;
                    }

                    foreach (var p in needed)
                    {
                        output.WriteLine($"  + {p.Folder}: {p.Title}");
                    }

                    if (!args.Contains("--ano") && !host.Confirm($"Nainštalovať {needed.Count} ovládačov?"))
                    {
                        output.WriteLine("Nič som nezmenil.");
                        return 0;
                    }

                    var started = DateTimeOffset.UtcNow;
                    using var awake = Awake.While("inštaluje ovládače");
                    var results = new List<(DriverWork.Installed, double)>();
                    foreach (var p in needed)
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var result = DriverWork.Install(folder, p);
                        results.Add((result, DriverWork.Seconds(watch)));
                        output.WriteLine($"  {(result.Ok ? "✓" : "✗")} {p.Folder}: {result.Message}");
                    }

                    WriteJournal(host.JournalPath, DriverWork.Record(backup, fingerprint, started, results));
                    var failed = results.Count(r => !r.Item1.Ok);
                    var reboot = results.Any(r => r.Item1.Reboot);
                    output.WriteLine($"Hotovo: {results.Count - failed} z {results.Count} ovládačov.{(reboot ? " Windows žiada reštart." : "")}");
                    return failed == 0 ? 0 : 1;
                }

            default:
                return Wrong($"Neznámy príkaz ovladace {action} (možné: priecinok, zaloha, overit, obnov).", output);
        }
    }

    /// <summary>The argument at the place, when it is not an option (--…).</summary>
    private static string? Positional(string[] args, int at) =>
        args.Length > at && !args[at].StartsWith("--", StringComparison.Ordinal) ? args[at] : null;

    [SupportedOSPlatform("windows")]
    private static (string? Model, string Edition, string Name) ThisBuild()
    {
        var model = DriverBuilds.Model(DriverPlace.ThisHardware());
        var edition = DriverPlace.ThisEdition();
        return (model, edition, DriverBuilds.Name(model, edition));
    }

    /// <summary>The backup to install from: the folder given (a backup, or a folder of backups to choose from), else the
    /// folder of backups – the backup of this build (same model, the same Windows first, the newest).</summary>
    [SupportedOSPlatform("windows")]
    private static FoundBackup? ChooseBackup(string? given, TextWriter output)
    {
        string root;
        if (given is not null)
        {
            root = Path.GetFullPath(given);
        }
        else if (DriverPlace.Root() is { } r)
        {
            root = r.Folder;
            output.WriteLine($"Priečinok záloh: {root} ({r.How}).");
        }
        else
        {
            output.WriteLine("Chyba: pripoj USB kľúč so zálohami (" + DriverBuilds.DefaultFolderOnStick + ") alebo nastav priečinok záloh: polaris-agent ovladace priecinok CESTA");
            return null;
        }

        var found = DriverPlace.Find(root, output);
        if (found.Count == 1 && string.Equals(found[0].Folder, root, StringComparison.OrdinalIgnoreCase))
        {
            return found[0];
        }

        var build = ThisBuild();
        var chosen = DriverBuilds.Choose(found, build.Model, build.Edition);
        if (chosen is null)
        {
            output.WriteLine($"Chyba: v {root} nie je záloha zostavy {build.Name} (záloh: {found.Count}). Zálohy:");
            foreach (var f in found)
            {
                output.WriteLine("  " + Path.GetFileName(f.Folder));
            }

            output.WriteLine("Inú zálohu nainštaluješ s jej priečinkom: polaris-agent ovladace obnov CESTA\\K\\ZALOHE");
            return null;
        }

        output.WriteLine($"Záloha tejto zostavy: {Path.GetFileName(chosen.Folder)} (z {chosen.Backup.CreatedUtc.ToLocalTime():d.M.yyyy}).");
        return chosen;
    }

    private static int Wrong(string message, TextWriter output)
    {
        output.WriteLine("Chyba: " + message);
        return 2;
    }
}
