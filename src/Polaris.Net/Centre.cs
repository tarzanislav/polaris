namespace Polaris.Net;

/// <summary>What a centre decides to do with a signed task that reached it. The centre never invents a task – it only
/// checks the console's signature and right, records it in the log and passes it to the PC.</summary>
public enum Acceptance
{
    Taken,
    UnknownConsole,
    BadSignature,
    NotAllowed,
    UnknownPc,
    Expired,
    UnknownTask,
}

/// <summary>One centre (Linux or Windows): it holds the fleet and the shared log, checks tasks from consoles and records
/// what happens. Both centres run the same code; which one a PC talks to does not matter.</summary>
public sealed class Centre(string name, SigningKey ownKey, Fleet fleet, EventLog log)
{
    public string Name { get; } = name;

    public Fleet Fleet { get; } = fleet;

    public EventLog Log { get; } = log;

    /// <summary>Checks a task a console sent and, if good, records it. The PC later pulls it and checks it again itself.</summary>
    public Acceptance Receive(SignedTask signed, DateTimeOffset now)
    {
        var task = signed.Task;
        if (!Tasks.IsKnown(task.Kind))
        {
            return Acceptance.UnknownTask;
        }

        var console = Fleet.FindConsole(task.Console);
        if (console is null)
        {
            return Acceptance.UnknownConsole;
        }

        using (var key = VerifyingKey.FromSubjectPublicKeyInfo(console.PublicKey))
        {
            if (!key.Verify(task.BytesToSign(), signed.Signature))
            {
                return Acceptance.BadSignature;
            }
        }

        if (!console.MaySend(task.Kind))
        {
            return Acceptance.NotAllowed;
        }

        if (Fleet.Find(task.Pc) is null)
        {
            return Acceptance.UnknownPc;
        }

        if (now > task.ExpiresUtc)
        {
            return Acceptance.Expired;
        }

        Log.Write(Name, ownKey, "uloha", $"{console.Name} ({console.Role}): {Tasks.Describe(task.Kind)}"
            + (task.Argument.Length > 0 ? $" – {task.Argument}" : ""), task.Pc, now);
        return Acceptance.Taken;
    }

    /// <summary>A PC reported in: remember when, and what it says about itself.</summary>
    public void PcReported(Guid id, PcState state, DateTimeOffset now)
    {
        if (Fleet.Find(id) is { } pc)
        {
            pc.LastSeenUtc = now;
            pc.State = state;
        }
    }
}
