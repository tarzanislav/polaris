using System.Runtime.Versioning;
using System.Text;
using Polaris.Core;
using Polaris.Recipes;
using Polaris.Windows;

namespace Polaris.Agent;

/// <summary>The agent as a Windows service (PolarisAgent, LocalSystem): it watches the sessions of shared accounts and
/// cleans their profiles by their rules. The program lives in Program Files, where only administrators can write – a
/// service started from a folder a pupil can change would be a way to take over the PC.</summary>
[SupportedOSPlatform("windows")]
public static class AgentService
{
    public const string Name = "PolarisAgent";
    public const string DisplayName = "POLARIS agent";
    public const string Description = "Udržiava PC podľa receptu POLARIS a čistí relácie zdieľaných kont žiakov.";
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    public static string InstalledProgram =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Product.Name, "polaris-agent.exe");

    /// <summary>Runs as the service; false when the program was not started by the service control manager.</summary>
    public static bool RunAsService(AgentHost host) => ServiceHost.Run(Name, stop => Loop(host, stop));

    /// <summary>One round every <see cref="Interval"/>: every rule of a session, cleaning when it is time. A failure is
    /// written to the journal once (not every round) and the next round tries again.</summary>
    public static void Loop(AgentHost host, CancellationToken stop)
    {
        var journal = (RunRecord run) => Commands.WriteJournal(host.JournalPath, run);
        var state = new StoreState();
        var store = Store(host, state);
        using var meter = new LogonMeter(journal, host.Clock);
        var door = new Thread(() => StorePipe.Serve(store, Log, stop, ready: meter.Ready)) { IsBackground = true, Name = "Školský obchod" };
        door.Start();
        var icons = new Thread(() => WatchIcons(host, stop)) { IsBackground = true, Name = "Ikona žiaka" };
        icons.Start();
        var centre = new Thread(() => FollowCentre(host, stop)) { IsBackground = true, Name = "Centrum" };
        centre.Start();

        var lastMessage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var maintenance = new MaintenanceHold(Awake.Request);
        try
        {
            new ProfileVault().UnlockLeftovers(); // a restore cut by a power loss never leaves an account locked
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Commands.WriteJournal(host.JournalPath, Simple("(trezor)", "trezor", "trezor profilu", false, "chyba: " + e.Message, host.Clock()));
        }

        var startup = new StartupWatch(host);
        while (!stop.IsCancellationRequested)
        {
            if (maintenance.Update(MaintenanceStore.Until(), host.Clock()) is { } change)
            {
                Commands.WriteJournal(host.JournalPath, Simple("(údržba)", "udrzba", "údržba", true, change, host.Clock()));
            }

            startup.Look();

            foreach (var (account, mode) in SessionRules.Read())
            {
                var started = host.Clock();
                SessionCleaner.Result result;
                try
                {
                    result = SessionCleaner.Clean(account, mode, DateTime.Now, beforeDelete: store.RemoveFor);
                    if (!result.Cleaned && result.Message == SessionCleaner.AlreadyClean && Leftovers(state, account) && store.RemoveFor(account) is { } note)
                    {
                        // a removal that failed before the profile was deleted is tried again (the profile is gone already)
                        result = note.Contains("neodstránené", StringComparison.Ordinal) ? new SessionCleaner.Result(false, "chyba: " + note) : new SessionCleaner.Result(true, note);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    result = new SessionCleaner.Result(false, "chyba: " + e.Message);
                }

                var failed = result.Message.StartsWith("chyba", StringComparison.Ordinal);
                if (result.Cleaned || (failed && (!lastMessage.TryGetValue(account, out var last) || last != result.Message)))
                {
                    Commands.WriteJournal(host.JournalPath, new RunRecord
                    {
                        StartedUtc = started,
                        FinishedUtc = host.Clock(),
                        Recipe = "(relácia)",
                        Results = [new ChangeResult(account, $"relácia konta {account}", ChangeKind.Remove, result.Cleaned, result.Message, 0)],
                    });
                }

                lastMessage[account] = result.Message;
            }

            stop.WaitHandle.WaitOne(Interval);
        }

        maintenance.Update(null, host.Clock());
        door.Join(TimeSpan.FromSeconds(10));
        icons.Join(TimeSpan.FromSeconds(10));
        centre.Join(TimeSpan.FromSeconds(10));

        void Log(string message) => journal(new RunRecord
        {
            StartedUtc = host.Clock(),
            FinishedUtc = host.Clock(),
            Recipe = "(Školský obchod)",
            Results = [new ChangeResult("obchod", "Školský obchod", ChangeKind.Change, false, message, 0)],
        });
    }

    /// <summary>Every <see cref="IconWatch.Look"/>: the pupil's icon runs in every signed-in session; a fallen one is
    /// started again. When the service stops, the icons it started end.</summary>
    private static void WatchIcons(AgentHost host, CancellationToken stop)
    {
        var program = IconLauncher.InstalledProgram;
        var watch = new IconWatch(
            session => IconLauncher.Start(session, program),
            note => Commands.WriteJournal(host.JournalPath, new RunRecord
            {
                StartedUtc = host.Clock(),
                FinishedUtc = host.Clock(),
                Recipe = "(ikona žiaka)",
                Results = [new ChangeResult("ikona", "ikona žiaka", ChangeKind.Install, false, note, 0)],
            }));
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (File.Exists(program))
                {
                    watch.Check(IconLauncher.ActiveSessions(), host.Clock());
                }

                stop.WaitHandle.WaitOne(IconWatch.Look);
            }
        }
        finally
        {
            watch.StopAll();
        }
    }

    /// <summary>Every minute: the profile the timetable wants is applied when it should be; every five minutes the plan is
    /// asked from the centre. A failure is written to the journal once, not every round.</summary>
    private static void FollowCentre(AgentHost host, CancellationToken stop)
    {
        CentreSync? sync = null;
        CentreSettings? syncFor = null;
        var lastFetch = DateTimeOffset.MinValue;
        string? lastProblem = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (Centre.Settings() is { } settings)
                {
                    if (sync is null || settings != syncFor)
                    {
                        (sync, syncFor, lastFetch) = (Centre.Sync(settings, host), settings, DateTimeOffset.MinValue);
                    }

                    if (host.Clock() - lastFetch >= Centre.FetchEvery)
                    {
                        lastFetch = host.Clock();
                        var outcome = sync.Fetch(Centre.Sequence());
                        if (outcome.Changed)
                        {
                            Centre.SetSequence(outcome.Sequence);
                            lastProblem = null;
                            Note(host, true, outcome.Message);
                        }
                        else if (outcome.Message != "plán sa nezmenil" && outcome.Message != lastProblem)
                        {
                            lastProblem = outcome.Message;
                            Note(host, false, outcome.Message);
                        }
                    }

                    if (Centre.Tick(sync, host, DateTime.Now) is { } run)
                    {
                        Commands.WriteJournal(host.JournalPath, run);
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (e.Message != lastProblem)
                {
                    lastProblem = e.Message;
                    Note(host, false, "chyba: " + e.Message);
                }
            }

            stop.WaitHandle.WaitOne(Centre.TickEvery);
        }
    }

    private static void Note(AgentHost host, bool ok, string message) =>
        Commands.WriteJournal(host.JournalPath, Simple("(centrum)", "centrum", "plán z centra", ok, message, host.Clock()));

    /// <summary>A journal record of one line.</summary>
    public static RunRecord Simple(string recipe, string id, string title, bool ok, string message, DateTimeOffset? at = null) => new()
    {
        StartedUtc = at ?? DateTimeOffset.UtcNow,
        FinishedUtc = at ?? DateTimeOffset.UtcNow,
        Recipe = recipe,
        Results = [new ChangeResult(id, title, ChangeKind.Change, ok, message, 0)],
    };

    /// <summary>Every <see cref="Every"/>: the kept list of programs allowed to start by themselves is checked again –
    /// a program installed since then (by a recipe, the store or a person) is switched off too. A failure is written once.</summary>
    private sealed class StartupWatch(AgentHost host)
    {
        public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

        private readonly StartupMemory _memory = new();
        private DateTimeOffset _last = DateTimeOffset.MinValue;
        private string? _lastProblem;

        public void Look()
        {
            var now = host.Clock();
            if (now - _last < Every)
            {
                return;
            }

            _last = now;
            try
            {
                if (StartupRule.Read() is not { } allow)
                {
                    return;
                }

                var done = StartupWork.Enforce(allow, _memory, now);
                _lastProblem = null;
                if (done.Count > 0)
                {
                    Commands.WriteJournal(host.JournalPath, Simple("(spúšťanie)", "spustanie", "automatické spúšťanie programov", true, string.Join(", ", done), now));
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (e.Message != _lastProblem)
                {
                    _lastProblem = e.Message;
                    Commands.WriteJournal(host.JournalPath, Simple("(spúšťanie)", "spustanie", "automatické spúšťanie programov", false, "chyba: " + e.Message, now));
                }
            }
        }
    }

    /// <summary>The School store of this PC, as the service runs it.</summary>
    public static StoreWork Store(AgentHost host, StoreState state) =>
        new(state, new ProgramHandler(AppContext.BaseDirectory), run => Commands.WriteJournal(host.JournalPath, run), host.Clock,
            account => SessionRules.Read().TryGetValue(account, out var rule) && rule != SessionCleaning.Never,
            new StartMenuShortcuts());

    private static bool Leftovers(StoreState state, string account) =>
        state.Installed().Any(i => string.Equals(i.Account, account, StringComparison.OrdinalIgnoreCase));

    /// <summary>Copies this program to Program Files and registers the service (or updates it).</summary>
    public static string Install(string currentProgram, TextWriter output)
    {
        var target = InstalledProgram;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var running = ServiceManager.Exists(Name);
        if (running)
        {
            try
            {
                ServiceManager.SetRunning(Name, false, TimeSpan.FromSeconds(30));
            }
            catch (InvalidOperationException)
            {
            }
            catch (SystemCallException)
            {
            }
        }

        if (!string.Equals(Path.GetFullPath(currentProgram), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(currentProgram, target, overwrite: true);
            var icon = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(currentProgram))!, IconLauncher.ProgramName);
            if (File.Exists(icon))
            {
                File.Copy(icon, IconLauncher.InstalledProgram, overwrite: true);
                output.WriteLine("Ikona žiaka: služba ju spustí v každej prihlásenej relácii a padnutú spustí znova.");
            }
        }

        ServiceManager.Install(Name, DisplayName, Description, $"\"{target}\" sluzba");
        ServiceManager.SetRunning(Name, true, TimeSpan.FromSeconds(30));
        output.WriteLine($"Služba {DisplayName} beží z {target} (spúšťa sa automaticky, po páde sa obnoví).");
        return target;
    }

    public static void Uninstall(TextWriter output)
    {
        if (!ServiceManager.Exists(Name))
        {
            output.WriteLine("Služba POLARIS agent nie je nainštalovaná.");
            return;
        }

        ServiceManager.Remove(Name);
        output.WriteLine($"Služba {DisplayName} odstránená. Program ostal v {Path.GetDirectoryName(InstalledProgram)} – môžeš ho zmazať.");
    }

    /// <summary>Only the system and administrators may see and change the folder of the agent (its journal).</summary>
    public static void ProtectFolder(string folder)
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (Path.GetFullPath(folder).StartsWith(Path.GetFullPath(common), StringComparison.OrdinalIgnoreCase))
        {
            Security.Protect(folder);
        }
    }

    internal static Encoding Utf8 => new UTF8Encoding(false);
}
