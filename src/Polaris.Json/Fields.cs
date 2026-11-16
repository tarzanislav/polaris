using System.Globalization;

namespace Polaris.Json;

/// <summary>Reads the fields of one object into a program's own types. Every getter remembers the name it asked for;
/// <see cref="End"/> then refuses any other field and suggests the closest known name – a typo in a recipe never passes
/// silently. A missing field or null gives the default. A value of a wrong kind is an error with its position.</summary>
public sealed class JsonFields
{
    private readonly JsonObject _object;
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    public JsonFields(JsonNode node, string what)
    {
        _object = node as JsonObject ?? throw JsonError.At(node, $"{what} má byť objekt {{ … }}, je to {node.KindName}.");
        What = what;
    }

    /// <summary>What the object is, for messages (e.g. "prvok vlc").</summary>
    public string What { get; }

    public JsonObject Object => _object;

    public bool Has(string name)
    {
        _known.Add(name);
        return _object[name] is { Kind: not JsonKind.Null };
    }

    public JsonNode? Node(string name)
    {
        _known.Add(name);
        return _object[name] is { Kind: not JsonKind.Null } node ? node : null;
    }

    public string String(string name, string fallback = "") => OptionalString(name) ?? fallback;

    public string? OptionalString(string name) => Node(name) switch
    {
        null => null,
        JsonString s => s.Value,
        var other => throw Wrong(name, other, "text v úvodzovkách"),
    };

    public bool Bool(string name, bool fallback) => OptionalBool(name) ?? fallback;

    public bool? OptionalBool(string name) => Node(name) switch
    {
        null => null,
        JsonBool b => b.Value,
        var other => throw Wrong(name, other, "true alebo false"),
    };

    public int Int(string name, int fallback) => (int)Long(name, fallback, int.MinValue, int.MaxValue);

    public long Long(string name, long fallback, long min = long.MinValue, long max = long.MaxValue)
    {
        switch (Node(name))
        {
            case null:
                return fallback;
            case JsonNumber n when n.TryWhole(out var v):
                return v >= min && v <= max
                    ? v
                    : throw JsonError.At(n, $"pole „{name}“ ({What}): číslo {n.Text} je mimo rozsahu {min} až {max}.");
            case var other:
                throw Wrong(name, other, "celé číslo");
        }
    }

    public double Double(string name, double fallback) => Node(name) switch
    {
        null => fallback,
        JsonNumber n => n.ToDouble(),
        var other => throw Wrong(name, other, "číslo"),
    };

    /// <summary>A name of an enum value, without regard to the size of letters; the error lists the allowed names.</summary>
    public T Enum<T>(string name, T fallback)
        where T : struct, Enum => OptionalEnum<T>(name) ?? fallback;

    public T? OptionalEnum<T>(string name)
        where T : struct, Enum
    {
        var node = Node(name);
        if (node is null)
        {
            return null;
        }

        if (node is JsonString s)
        {
            foreach (var candidate in System.Enum.GetNames<T>())
            {
                if (string.Equals(candidate, s.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return System.Enum.Parse<T>(candidate);
                }
            }
        }

        throw JsonError.At(node, $"pole „{name}“ ({What}) môže byť len {string.Join(", ", System.Enum.GetNames<T>())}.");
    }

    public DateTimeOffset Time(string name, DateTimeOffset fallback = default)
    {
        var node = Node(name);
        return node switch
        {
            null => fallback,
            JsonString s when DateTimeOffset.TryParse(s.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) => t,
            _ => throw Wrong(name, node, "čas v tvare 2026-11-16T08:00:00+00:00"),
        };
    }

    public JsonFields? Fields(string name, string what) => Node(name) is { } node ? new JsonFields(node, what) : null;

    public IReadOnlyList<JsonNode> Array(string name) => Node(name) switch
    {
        null => [],
        JsonArray a => a.Items,
        var other => throw Wrong(name, other, "zoznam [ … ]"),
    };

    public List<string> Strings(string name) =>
        Array(name).Select(n => n is JsonString s ? s.Value : throw JsonError.At(n, $"pole „{name}“ ({What}): položka má byť text v úvodzovkách.")).ToList();

    /// <summary>An object of texts: { "name": "value", … } in the written order.</summary>
    public Dictionary<string, string>? Map(string name)
    {
        var node = Node(name);
        if (node is null)
        {
            return null;
        }

        if (node is not JsonObject o)
        {
            throw Wrong(name, node, "objekt { \"meno\": \"hodnota\" }");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in o.Members)
        {
            map[m.Name] = m.Value is JsonString s ? s.Value : throw JsonError.At(m.Value, $"pole „{name}“ ({What}): hodnota „{m.Name}“ má byť text v úvodzovkách.");
        }

        return map;
    }

    /// <summary>Refuses fields nobody asked for: a typo or a field of another type of element.</summary>
    public void End()
    {
        foreach (var m in _object.Members)
        {
            if (!_known.Contains(m.Name))
            {
                var near = Nearest(m.Name);
                throw new JsonError(m.Line, m.Column, $"{What} nepozná pole „{m.Name}“{(near is null ? "." : $" – nemyslel si „{near}“?")}");
            }
        }
    }

    private JsonError Wrong(string name, JsonNode node, string expected) =>
        JsonError.At(node, $"pole „{name}“ ({What}) má byť {expected}, je to {node.KindName}.");

    /// <summary>The known name closest to a typo (at most a third of its letters different), or null.</summary>
    private string? Nearest(string typo)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var name in _known)
        {
            var d = Distance(typo.ToLowerInvariant(), name.ToLowerInvariant());
            if (d < bestDistance)
            {
                (best, bestDistance) = (name, d);
            }
        }

        return bestDistance <= Math.Max(1, typo.Length / 3) ? best : null;
    }

    /// <summary>Edits needed to make one word the other (insert, delete, change, swap of two neighbours).</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }
}
