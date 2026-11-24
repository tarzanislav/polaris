using System.Diagnostics;
using System.Runtime.Versioning;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>Memory one program takes: all its processes together.</summary>
public sealed record ProcessUse(string Name, long Bytes, int Count);

/// <summary>A measurement after a sign-in, as one record of the journal: how long it took until the desktop was ready
/// (the icon could take its place on the taskbar) and, two minutes later, how much memory the PC and the programs of
/// the session take – what the trezor of profiles and the quieter defaults should make smaller.</summary>
public static class Measurement
{
    public const string Recipe = "(meranie)";

    /// <summary>The journal record of a sign-in that was not measured (and why).</summary>
    public const string SkippedRecipe = "(prihlásenie)";
    public static readonly TimeSpan After = TimeSpan.FromMinutes(2);

    /// <summary>A sign-in longer ago than this is not measured (the icon started again later, not at the sign-in).</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(10);

    public const int Top = 10;

    /// <summary>When the icon has not said "ready" this long after a sign-in, the service measures without it (the time of
    /// the desktop is then not known, the memory still is).</summary>
    public static readonly TimeSpan WaitForIcon = TimeSpan.FromMinutes(3);

    public static RunRecord Record(
        string account,
        double? logonSeconds,
        (ulong Total, ulong Available)? memory,
        IReadOnlyList<ProcessUse> session,
        long services,
        DateTimeOffset started,
        DateTimeOffset now)
    {
        var results = new List<ChangeResult>
        {
            Line("prihlasenie", $"prihlásenie konta {account}", logonSeconds is { } seconds
                ? $"pracovná plocha za {Polaris.Core.Format.Decimal(seconds, 0)} s"
                : $"čas plochy nezmeraný (ikona sa do {WaitForIcon.TotalMinutes:0} min neozvala)"),
        };
        if (memory is { } m && m.Total > 0)
        {
            var used = (long)(m.Total - m.Available);
            results.Add(Line("pamat", "pamäť PC", $"obsadené {Size(used)} z {Size((long)m.Total)} ({used * 100 / (long)m.Total} %)"));
        }

        results.Add(Line("relacia", $"programy relácie {account}", session.Count == 0
            ? "relácia už skončila"
            : $"spolu {Size(session.Sum(p => p.Bytes))} ({session.Sum(p => p.Count)} procesov)"));
        foreach (var p in session.Take(Top))
        {
            results.Add(Line("proces:" + p.Name, p.Count > 1 ? $"{p.Name} ×{p.Count}" : p.Name, Size(p.Bytes)));
        }

        results.Add(Line("sluzby", "služby a systém (relácia 0)", Size(services)));
        return new RunRecord { StartedUtc = started, FinishedUtc = now, Recipe = Recipe, Results = results };
    }

    /// <summary>Processes by program name, the biggest first.</summary>
    public static List<ProcessUse> Group(IEnumerable<(string Name, long Bytes)> processes) =>
        [.. processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessUse(g.First().Name, g.Sum(p => p.Bytes), g.Count()))
            .OrderByDescending(p => p.Bytes)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>MB under a gigabyte, GB with one decimal above (Slovak comma).</summary>
    public static string Size(long bytes)
    {
        const double MB = 1024 * 1024;
        return bytes < 1024 * MB
            ? Polaris.Core.Format.Decimal(bytes / MB, 0) + " MB"
            : Polaris.Core.Format.Decimal(bytes / (1024 * MB), 1) + " GB";
    }

    private static ChangeResult Line(string id, string title, string message) => new(id, title, ChangeKind.None, true, message, 0);
}

/// <summary>The service's side: the icon says when its session's desktop is ready; the time since the sign-in is taken
/// then, the memory two minutes later. Each sign-in once; a sign-in from before the service started is not measured,
/// unless the service started with Windows (the automatic sign-in may come first).</summary>
[SupportedOSPlatform("windows")]
public sealed class LogonMeter(Action<RunRecord> journal, Func<DateTimeOffset> clock) : IDisposable
{
    private readonly DateTimeOffset _serviceStarted = clock();

    /// <summary>The service started with Windows (not later, by hand or after an update): a sign-in a moment before it –
    /// the automatic sign-in is often quicker than the services – is still the sign-in after the start.</summary>
    private readonly bool _startedWithWindows = Environment.TickCount64 < (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
    private readonly Dictionary<int, DateTimeOffset> _measured = [];
    private readonly List<Timer> _timers = [];
    private readonly Lock _gate = new();

    /// <summary>The icon of the session says its desktop is ready: the time since the sign-in now, the memory later.</summary>
    public void Ready(int session)
    {
        if (Polaris.Windows.Logons.Of(session) is not { } logon)
        {
            Skipped(session, "Windows nepovedal čas prihlásenia relácie");
            return;
        }

        var now = clock();
        Start(logon, now - logon.LogonUtc, now, fromIcon: true);
    }

    /// <summary>Every look of the service at the sessions: a sign-in whose icon has not said "ready" in
    /// <see cref="Measurement.WaitForIcon"/> is measured without it – a measurement never depends on the icon alone.</summary>
    public void Seen(IEnumerable<int> sessions)
    {
        var now = clock();
        foreach (var session in sessions)
        {
            if (Polaris.Windows.Logons.Of(session) is { } logon && now - logon.LogonUtc >= Measurement.WaitForIcon)
            {
                Start(logon, null, now, fromIcon: false);
            }
        }
    }

    private void Start(Polaris.Windows.SessionLogon logon, TimeSpan? took, DateTimeOffset now, bool fromIcon)
    {
        var session = logon.Session;
        lock (_gate)
        {
            if (_measured.TryGetValue(session, out var last) && last == logon.LogonUtc)
            {
                return; // this sign-in is measured (or waits for its measurement) already
            }

            _measured[session] = logon.LogonUtc;
            if (!fromIcon && now - logon.LogonUtc > Measurement.Longest)
            {
                return; // a sign-in long before (the service started later): nothing to say about it
            }

            if (logon.LogonUtc < _serviceStarted - TimeSpan.FromSeconds(5) && !_startedWithWindows)
            {
                Skipped(session, "služba sa spustila až po prihlásení (napr. po aktualizácii) – meria sa nasledujúce prihlásenie");
                return;
            }

            var since = now - logon.LogonUtc;
            if (since > Measurement.Longest || since < TimeSpan.Zero)
            {
                Skipped(session, fromIcon
                    ? $"plocha hotová {since.TotalMinutes:0} min po prihlásení – mimo merania"
                    : $"prihlásenie pred {since.TotalMinutes:0} min – mimo merania");
                return;
            }

            // from the icon: the memory two minutes after the desktop was ready; without it: now (the sign-in is past)
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                Measure(logon, took?.TotalSeconds, now);
                lock (_gate)
                {
                    _timers.Remove(timer!);
                }

                timer!.Dispose();
            });
            _timers.Add(timer);
            timer.Change(fromIcon ? Measurement.After : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private void Skipped(int session, string why) =>
        journal(new RunRecord
        {
            StartedUtc = clock(),
            FinishedUtc = clock(),
            Recipe = Measurement.SkippedRecipe,
            Results = [new ChangeResult("meranie", $"meranie prihlásenia (relácia {session})", ChangeKind.None, true, "nezmerané: " + why, 0)],
        });

    public void Dispose()
    {
        lock (_gate)
        {
            _timers.ForEach(t => t.Dispose());
            _timers.Clear();
        }
    }

    private void Measure(Polaris.Windows.SessionLogon logon, double? seconds, DateTimeOffset ready)
    {
        try
        {
            var inSession = new List<(string, long)>();
            long services = 0;
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.SessionId == logon.Session)
                        {
                            inSession.Add((p.ProcessName, p.WorkingSet64));
                        }
                        else if (p.SessionId == 0)
                        {
                            services += p.WorkingSet64;
                        }
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // the process ended meanwhile
                    }
                }
            }

            (ulong, ulong)? memory = null;
            try
            {
                memory = Polaris.Windows.MemoryStatus.Physical();
            }
            catch (Polaris.Windows.SystemCallException)
            {
                // without the whole: the programs are still worth the record
            }

            journal(Measurement.Record(logon.Account, seconds, memory, Measurement.Group(inSession), services, ready, clock()));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // a measurement never brings the service down
        }
    }
}
