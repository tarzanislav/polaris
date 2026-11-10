using System.Globalization;
using System.Text.Json.Serialization;

namespace Polaris.Net;

/// <summary>One signed task for a PC. It names the PC, the kind of task and a little text argument (a profile name, a
/// number of restarts, a message). Its own number and a moment of expiry stop an old signed task from being replayed.</summary>
public sealed class TaskMessage
{
    /// <summary>Which PC the task is for (the PC's stable identifier).</summary>
    public Guid Pc { get; set; }

    public TaskKind Kind { get; set; }

    /// <summary>The little argument: a profile name, "3" restarts, the text of a message. Never a command.</summary>
    public string Argument { get; set; } = "";

    /// <summary>The console that signed it (the fingerprint of its key).</summary>
    public string Console { get; set; } = "";

    /// <summary>Rising per console; a PC refuses a number it has already seen.</summary>
    public long Sequence { get; set; }

    public DateTimeOffset IssuedUtc { get; set; }

    /// <summary>After this moment the task is stale and refused, even if signed correctly.</summary>
    public DateTimeOffset ExpiresUtc { get; set; }

    /// <summary>The exact bytes that are signed – a fixed order with length-framed fields, independent of the JSON text.</summary>
    public byte[] BytesToSign() => Framing.Build(
        "POLTASK2"u8,
        Pc.ToString("N"),
        ((int)Kind).ToString(CultureInfo.InvariantCulture),
        Argument,
        Console,
        Sequence.ToString(CultureInfo.InvariantCulture),
        IssuedUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        ExpiresUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
}

/// <summary>A task with its signature, as it travels and is stored.</summary>
public sealed class SignedTask
{
    public TaskMessage Task { get; set; } = new();

    public byte[] Signature { get; set; } = [];

    [JsonIgnore]
    public TaskKind Kind => Task.Kind;
}

public enum TaskVerdict
{
    Ok,
    BadSignature,
    UnknownConsole,
    Expired,
    AlreadySeen,
    NotForThisPc,
    NotAllowed,
    UnknownTask,
}

/// <summary>Checks a signed task on the PC: the signature, the console is trusted and allowed, the task is for this PC,
/// not stale, not a replay. Only <see cref="TaskVerdict.Ok"/> is carried out.</summary>
public sealed class TaskChecker(Guid thisPc, Func<string, VerifyingKey?> consoleKey, Func<string, TaskKind, bool> consoleAllowed)
{
    private readonly Dictionary<string, long> _lastSeen = new(StringComparer.Ordinal);

    /// <summary>Remembers the highest accepted number per console (so a restart keeps rejecting replays).</summary>
    public IReadOnlyDictionary<string, long> HighWater => _lastSeen;

    public void Restore(string console, long sequence) => _lastSeen[console] = sequence;

    public TaskVerdict Check(SignedTask signed, DateTimeOffset now)
    {
        var task = signed.Task;
        if (!Tasks.IsKnown(task.Kind))
        {
            return TaskVerdict.UnknownTask;
        }

        if (task.Pc != thisPc)
        {
            return TaskVerdict.NotForThisPc;
        }

        using var key = consoleKey(task.Console);
        if (key is null)
        {
            return TaskVerdict.UnknownConsole;
        }

        if (!key.Verify(task.BytesToSign(), signed.Signature))
        {
            return TaskVerdict.BadSignature;
        }

        if (now > task.ExpiresUtc)
        {
            return TaskVerdict.Expired;
        }

        if (!consoleAllowed(task.Console, task.Kind))
        {
            return TaskVerdict.NotAllowed;
        }

        if (_lastSeen.TryGetValue(task.Console, out var last) && task.Sequence <= last)
        {
            return TaskVerdict.AlreadySeen;
        }

        _lastSeen[task.Console] = task.Sequence;
        return TaskVerdict.Ok;
    }
}
