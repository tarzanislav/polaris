namespace Polaris.Agent;

/// <summary>Keeps the pupil's icon running in every signed-in session: when it is missing or has fallen, it is started
/// again at the next look. An icon that keeps falling right after its start is not restarted in a loop – after
/// <see cref="FastFailures"/> quick falls it waits <see cref="Pause"/>, and that is written to the journal once.
/// No Windows here: the decisions are checked on any system, the hands are given in.</summary>
public sealed class IconWatch(Func<int, IIconProcess?> start, Action<string> journal)
{
    public static readonly TimeSpan Look = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan QuickFall = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(5);
    public const int FastFailures = 3;

    private readonly Dictionary<int, State> _sessions = [];

    private sealed class State
    {
        public IIconProcess? Process;
        public DateTimeOffset Started;
        public int QuickFalls;
        public DateTimeOffset WaitUntil;
        public bool Reported;
    }

    /// <summary>One look: <paramref name="sessions"/> are the sessions with a signed-in user now.</summary>
    public void Check(IReadOnlyCollection<int> sessions, DateTimeOffset now)
    {
        foreach (var gone in _sessions.Keys.Where(id => !sessions.Contains(id)).ToList())
        {
            _sessions[gone].Process?.Dispose(); // the session ended, its icon with it
            _sessions.Remove(gone);
        }

        foreach (var id in sessions)
        {
            if (!_sessions.TryGetValue(id, out var s))
            {
                _sessions[id] = s = new State();
            }

            if (s.Process is { HasExited: false })
            {
                if (now - s.Started > QuickFall)
                {
                    s.QuickFalls = 0; // it has run long enough: earlier falls are forgotten
                    s.Reported = false;
                }

                continue;
            }

            if (s.Process is not null)
            {
                s.QuickFalls = now - s.Started < QuickFall ? s.QuickFalls + 1 : 0;
                s.Process.Dispose();
                s.Process = null;
                if (s.QuickFalls >= FastFailures)
                {
                    s.WaitUntil = now + Pause;
                    s.QuickFalls = 0;
                    if (!s.Reported)
                    {
                        journal($"ikona v relácii {id} padá hneď po štarte – ďalší pokus o {Pause.TotalMinutes:0} min");
                        s.Reported = true;
                    }
                }
            }

            if (now < s.WaitUntil)
            {
                continue;
            }

            try
            {
                s.Process = start(id);
                s.Started = now;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                s.WaitUntil = now + Pause;
                if (!s.Reported)
                {
                    journal($"ikonu v relácii {id} sa nepodarilo spustiť: {e.Message}");
                    s.Reported = true;
                }
            }
        }
    }

    /// <summary>The service stops: the icons it started end too.</summary>
    public void StopAll()
    {
        foreach (var s in _sessions.Values)
        {
            if (s.Process is { HasExited: false } p)
            {
                p.Stop();
            }

            s.Process?.Dispose();
        }

        _sessions.Clear();
    }
}

/// <summary>A started icon as the watch sees it.</summary>
public interface IIconProcess : IDisposable
{
    bool HasExited { get; }

    void Stop();
}
