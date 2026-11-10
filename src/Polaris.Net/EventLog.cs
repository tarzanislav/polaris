using System.Text;
using System.Text.Json.Serialization;

namespace Polaris.Net;

/// <summary>The shared memory of the two centres (Linux and Windows): a log to which entries are only added. Each entry
/// carries its origin, its own number and a signature, so the other centre can take it over and no one can quietly
/// change the past. The two centres swap entries until they hold the same ones (design 4.16).</summary>
public sealed class LogEntry
{
    /// <summary>Who wrote it: a centre name or a console fingerprint.</summary>
    public string Origin { get; set; } = "";

    /// <summary>Rising per origin.</summary>
    public long Sequence { get; set; }

    public DateTimeOffset Utc { get; set; }

    public string Kind { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>The PC the entry is about, when it is about one.</summary>
    public Guid? Pc { get; set; }

    public byte[] Signature { get; set; } = [];

    public byte[] BytesToSign()
    {
        var text = $"{Origin}\n{Sequence}\n{Utc.ToUnixTimeMilliseconds()}\n{Kind}\n{Pc:N}\n{Text}";
        var body = Encoding.UTF8.GetBytes(text);
        var framed = new byte[8 + body.Length];
        "POLLOG01"u8.CopyTo(framed);
        body.CopyTo(framed, 8);
        return framed;
    }

    [JsonIgnore]
    public (string, long) Key => (Origin, Sequence);
}

/// <summary>An append-only log kept by one centre. Entries are identified by (origin, sequence); taking over another
/// centre's entries never overwrites or reorders what is already here.</summary>
public sealed class EventLog
{
    private readonly List<LogEntry> _entries = [];
    private readonly HashSet<(string, long)> _have = [];
    private readonly Dictionary<string, long> _next = new(StringComparer.Ordinal);

    public IReadOnlyList<LogEntry> Entries => _entries;

    /// <summary>Writes a new entry of this centre and signs it.</summary>
    public LogEntry Write(string origin, SigningKey key, string kind, string text, Guid? pc, DateTimeOffset now)
    {
        var seq = _next.GetValueOrDefault(origin) + 1;
        var entry = new LogEntry { Origin = origin, Sequence = seq, Utc = now, Kind = kind, Text = text, Pc = pc };
        entry.Signature = key.Sign(entry.BytesToSign());
        Add(entry);
        return entry;
    }

    /// <summary>Takes over an entry from the other centre. Needs a matching signature; a duplicate is ignored.</summary>
    public bool Accept(LogEntry entry, Func<string, VerifyingKey?> originKey)
    {
        if (_have.Contains(entry.Key))
        {
            return false;
        }

        using var key = originKey(entry.Origin);
        if (key is null || !key.Verify(entry.BytesToSign(), entry.Signature))
        {
            return false;
        }

        Add(entry);
        return true;
    }

    /// <summary>Entries this log is missing compared with what the other centre last had per origin.</summary>
    public IEnumerable<LogEntry> Since(IReadOnlyDictionary<string, long> theirHighWater) =>
        _entries.Where(e => e.Sequence > theirHighWater.GetValueOrDefault(e.Origin));

    /// <summary>The highest sequence this log holds per origin (what it tells the other centre it has).</summary>
    public Dictionary<string, long> HighWater() => new(_next, StringComparer.Ordinal);

    private void Add(LogEntry entry)
    {
        _entries.Add(entry);
        _have.Add(entry.Key);
        if (entry.Sequence > _next.GetValueOrDefault(entry.Origin))
        {
            _next[entry.Origin] = entry.Sequence;
        }
    }
}
