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
    public static readonly TimeSpan After = TimeSpan.FromMinutes(2);

    /// <summary>A sign-in longer ago than this is not measured (the icon started again later, not at the sign-in).</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(10);

    public const int Top = 10;

    public static RunRecord Record(
        string account,
        double logonSeconds,
        (ulong Total, ulong Available)? memory,
        IReadOnlyList<ProcessUse> session,
        long services,
        DateTimeOffset started,
        DateTimeOffset now)
    {
        var results = new List<ChangeResult>
        {
            Line("prihlasenie", $"prihlásenie konta {account}", $"pracovná plocha za {Polaris.Core.Format.Decimal(logonSeconds, 0)} s"),
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
/// then, the memory two minutes later. Each sign-in once; a sign-in from before the service started is not measured.</summary>
[SupportedOSPlatform("windows")]
public sealed class LogonMeter(Action<RunRecord> journal, Func<DateTimeOffset> clock) : IDisposable
{
    private readonly DateTimeOffset _serviceStarted = clock();
    private readonly Dictionary<int, DateTimeOffset> _measured = [];
    private readonly List<Timer> _timers = [];
    private readonly Lock _gate = new();

    public void Ready(int session)
    {
        if (Polaris.Windows.Logons.Of(session) is not { } logon)
        {
            return;
        }

        var now = clock();
        var took = now - logon.LogonUtc;
        lock (_gate)
        {
            if (logon.LogonUtc < _serviceStarted || took > Measurement.Longest || took < TimeSpan.Zero
                || (_measured.TryGetValue(session, out var last) && last == logon.LogonUtc))
            {
                return;
            }

            _measured[session] = logon.LogonUtc;
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                Measure(logon, took.TotalSeconds, now);
                lock (_gate)
                {
                    _timers.Remove(timer!);
                }

                timer!.Dispose();
            });
            _timers.Add(timer);
            timer.Change(Measurement.After, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timers.ForEach(t => t.Dispose());
            _timers.Clear();
        }
    }

    private void Measure(Polaris.Windows.SessionLogon logon, double seconds, DateTimeOffset ready)
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
