namespace Polaris.Net;

/// <summary>A console makes tasks here: it fills in its fingerprint, a rising number and the moment of expiry, and signs.
/// The number rises on its own so two tasks of the same console are never mixed up.</summary>
public sealed class TaskIssuer(string consoleFingerprint, SigningKey key)
{
    private long _sequence;

    /// <summary>Continues the numbers after a restart of the console (from what the centre last recorded).</summary>
    public void ContinueFrom(long sequence) => _sequence = Math.Max(_sequence, sequence);

    public SignedTask Make(Guid pc, TaskKind kind, string argument, DateTimeOffset now, TimeSpan validFor)
    {
        var task = new TaskMessage
        {
            Pc = pc,
            Kind = kind,
            Argument = argument,
            Console = consoleFingerprint,
            Sequence = ++_sequence,
            IssuedUtc = now,
            ExpiresUtc = now + validFor,
        };
        return new SignedTask { Task = task, Signature = key.Sign(task.BytesToSign()) };
    }
}

/// <summary>Turns the little text argument of a task into numbers, and back, in one place (so the agent and the console
/// read it the same way).</summary>
public static class TaskArgument
{
    public static string Restarts(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryRestarts(string argument, out int count) =>
        int.TryParse(argument, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out count) && count > 0;

    public static string Until(DateTimeOffset moment) => moment.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryUntil(string argument, out DateTimeOffset moment)
    {
        if (long.TryParse(argument, out var unix))
        {
            moment = DateTimeOffset.FromUnixTimeSeconds(unix);
            return true;
        }

        moment = default;
        return false;
    }
}
