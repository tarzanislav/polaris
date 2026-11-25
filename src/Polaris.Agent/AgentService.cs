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
        var icons = new Thread(() => WatchIcons(host, stop, meter)) { IsBackground = true, Name = "Ikona žiaka" };
        icons.Start();
        var centre = new Thread(() => FollowCentre(host, stop)) { IsBackground = true, Name = "Centrum" };
        centre.Start();
        var reports = new Thread(() => ReportToCentre(host, stop)) { IsBackground = true, Name = "Hlásenia" };
        reports.Start();

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
        var autoLogon = new AutoLogonWatch(host);
        while (!stop.IsCancellationRequested)
        {
            if (maintenance.Update(MaintenanceStore.Until(), host.Clock()) is { } change)
            {
                Commands.WriteJournal(host.JournalPath, Simple("(údržba)", "udrzba", "údržba", true, change, host.Clock()));
            }

            startup.Look();
            autoLogon.Look();

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

        if (Polaris.Windows.ServiceHost.ShuttingDown)
        {
            LastRound(host, store);
            autoLogon.Look();
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
    private static void WatchIcons(AgentHost host, CancellationToken stop, LogonMeter meter)
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
                var sessions = IconLauncher.ActiveSessions();
                if (File.Exists(program))
                {
                    watch.Check(sessions, host.Clock());
                }

                try
                {
                    meter.Seen(sessions);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // a measurement never stops the icons
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
    /// <summary>Every minute: a report to the centre when the journal grew (a recipe, a cleaning, a measurement) or 15
    /// minutes passed. A centre that cannot be reached is tried again the next minute; the problem goes to the journal once.</summary>
    private static void ReportToCentre(AgentHost host, CancellationToken stop)
    {
        var lastSent = DateTimeOffset.MinValue;
        long lastLength = -1;
        string? lastProblem = null;
        while (!stop.WaitHandle.WaitOne(TimeSpan.FromMinutes(1)))
        {
            try
            {
                if (PcLink.Id() is null)
                {
                    continue;
                }

                var length = File.Exists(host.JournalPath) ? new FileInfo(host.JournalPath).Length : 0;
                var now = host.Clock();
                if (length == lastLength && now - lastSent < PcLink.ReportEvery)
                {
                    continue;
                }

                var (ok, message) = PcLink.Report(host);
                if (ok)
                {
                    (lastSent, lastLength, lastProblem) = (now, File.Exists(host.JournalPath) ? new FileInfo(host.JournalPath).Length : 0, null);
                }
                else if (message != lastProblem)
                {
                    lastProblem = message;
                    Commands.WriteJournal(host.JournalPath, Simple("(centrum)", "hlasenie", "hlásenie centru", false, message, now));
                    lastLength = File.Exists(host.JournalPath) ? new FileInfo(host.JournalPath).Length : 0; // the line about it is no news
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // a report never stops the service
            }
        }
    }

    private static void FollowCentre(AgentHost host, CancellationToken stop)
    {
        CentreSync? sync = null;
        ClientUpdate? update = null;
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
                        update = new ClientUpdate(settings, Path.Combine(Packages.AgentFolder, "aktualizacia"), TrustStore.Read, host.Clock);
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

                        // a new client signed by a trusted console: it takes the place of this one (and restarts the service)
                        var (program, message) = update!.Look(Product.Version);
                        if (message.Length > 0 && message != lastProblem)
                        {
                            lastProblem = program is null ? message : null;
                            Commands.WriteJournal(host.JournalPath, Simple("(aktualizácia)", "aktualizacia", "aktualizácia klienta", program is not null, message, host.Clock()));
                        }

                        if (program is not null)
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program, "aktualizovat-sluzbu") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
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

    /// <summary>Windows shuts down and the users are signed out: the profiles of shared accounts are cleaned or put back from
    /// the trezor now, so the PC starts with them ready (and signs in by itself). Windows lets go of a profile a few
    /// seconds after the sign-out – the round waits for it at most a minute.</summary>
    private static void LastRound(AgentHost host, StoreWork store)
    {
        var until = host.Clock() + TimeSpan.FromMinutes(1);
        foreach (var (account, mode) in SessionRules.Read().Where(r => r.Value is SessionCleaning.AtSignOut or SessionCleaning.Restore))
        {
            try
            {
                if (Profiles.SidOf(account) is not { } sid)
                {
                    continue;
                }

                while (Profiles.State(sid).Loaded && host.Clock() < until)
                {
                    Thread.Sleep(1000);
                }

                var result = SessionCleaner.Clean(account, mode, DateTime.Now, beforeDelete: store.RemoveFor);
                if (result.Cleaned)
                {
                    Commands.WriteJournal(host.JournalPath, Simple("(relácia)", account, $"relácia konta {account} pri vypínaní", true, result.Message, host.Clock()));
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Commands.WriteJournal(host.JournalPath, Simple("(relácia)", account, $"relácia konta {account} pri vypínaní", false, "chyba: " + e.Message, host.Clock()));
            }
        }
    }

    /// <summary>Every round: the automatic sign-in of Windows is on only when the profile of its account is ready.</summary>
    private sealed class AutoLogonWatch(AgentHost host)
    {
        private string? _lastProblem;

        public void Look()
        {
            try
            {
                // only a change of the rule goes to the journal, not every sign-in and sign-out
                if (AutoLogon.Update(SessionCleaner.Pending) is { } change && change.Contains("vypnuté", StringComparison.Ordinal))
                {
                    Commands.WriteJournal(host.JournalPath, Simple("(prihlásenie)", "autoprihlasenie", "automatické prihlásenie", true, change, host.Clock()));
                }

                _lastProblem = null;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (e.Message != _lastProblem)
                {
                    _lastProblem = e.Message;
                    Commands.WriteJournal(host.JournalPath, Simple("(prihlásenie)", "autoprihlasenie", "automatické prihlásenie", false, "chyba: " + e.Message, host.Clock()));
                }
            }
        }
    }

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

        try
        {
            if (!string.Equals(Path.GetFullPath(currentProgram), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                // the stopped service may still be ending for a moment: a few tries
                Retry(() => File.Copy(currentProgram, target, overwrite: true));
                var icon = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(currentProgram))!, IconLauncher.ProgramName);
                try
                {
                    if (File.Exists(icon))
                    {
                        Retry(() => File.Copy(icon, IconLauncher.InstalledProgram, overwrite: true));
                        output.WriteLine("Ikona žiaka: služba ju spustí v každej prihlásenej relácii a padnutú spustí znova.");
                    }
                    else if (typeof(AgentService).Assembly.GetManifestResourceStream(IconLauncher.ProgramName) is { } inside)
                    {
                        // the one-file installer carries the icon program inside
                        using (inside)
                        {
                            Retry(() =>
                            {
                                inside.Position = 0;
                                using var file = File.Create(IconLauncher.InstalledProgram);
                                inside.CopyTo(file);
                            });
                        }

                        output.WriteLine("Ikona žiaka: služba ju spustí v každej prihlásenej relácii a padnutú spustí znova.");
                    }
                }
                catch (IOException e)
                {
                    output.WriteLine("Ikona žiaka sa nedala nahradiť (" + e.Message + ") – ostáva predchádzajúca.");
                }
            }
        }
        finally
        {
            // whatever happened to the copy, the service runs again (the new program, or the one that was there)
            ServiceManager.Install(Name, DisplayName, Description, $"\"{target}\" sluzba");
            ServiceManager.SetRunning(Name, true, TimeSpan.FromSeconds(30));
        }

        output.WriteLine($"Služba {DisplayName} beží z {target} (spúšťa sa automaticky, po páde sa obnoví).");
        return target;
    }

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 15)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
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
