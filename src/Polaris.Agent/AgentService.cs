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
        var door = new Thread(() => StorePipe.Serve(store, Log, stop)) { IsBackground = true, Name = "Školský obchod" };
        door.Start();
        var icons = new Thread(() => WatchIcons(host, stop)) { IsBackground = true, Name = "Ikona žiaka" };
        icons.Start();

        var lastMessage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (!stop.IsCancellationRequested)
        {
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

        door.Join(TimeSpan.FromSeconds(10));
        icons.Join(TimeSpan.FromSeconds(10));

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

    /// <summary>The School store of this PC, as the service runs it.</summary>
    public static StoreWork Store(AgentHost host, StoreState state) =>
        new(state, new ProgramHandler(AppContext.BaseDirectory), run => Commands.WriteJournal(host.JournalPath, run), host.Clock,
            account => SessionRules.Read().TryGetValue(account, out var rule) && rule != SessionCleaning.Never);

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
