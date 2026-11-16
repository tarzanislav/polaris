using System.Globalization;

namespace Polaris.Json;

public enum JsonKind
{
    Object,
    Array,
    String,
    Number,
    Bool,
    Null,
}

/// <summary>A value of a JSON text. Values read from a text know where they started (line and character, from 1); values
/// made by a program have position 0.</summary>
public abstract class JsonNode(int line, int column)
{
    public int Line { get; } = line;

    public int Column { get; } = column;

    public abstract JsonKind Kind { get; }

    /// <summary>The kind in Slovak, for messages ("je to text").</summary>
    public string KindName => Kind switch
    {
        JsonKind.Object => "objekt { }",
        JsonKind.Array => "zoznam [ ]",
        JsonKind.String => "text",
        JsonKind.Number => "číslo",
        JsonKind.Bool => "true/false",
        _ => "null",
    };

    public static implicit operator JsonNode(string value) => new JsonString(value);

    public static implicit operator JsonNode(long value) => new JsonNumber(value);

    public static implicit operator JsonNode(bool value) => new JsonBool(value);
}

/// <summary>A member of an object with the position of its name.</summary>
public sealed record JsonMember(string Name, JsonNode Value, int Line, int Column);

/// <summary>An object: names in the order they were written or added; a name is there at most once.</summary>
public sealed class JsonObject(int line = 0, int column = 0) : JsonNode(line, column)
{
    private readonly List<JsonMember> _members = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public override JsonKind Kind => JsonKind.Object;

    public IReadOnlyList<JsonMember> Members => _members;

    public int Count => _members.Count;

    public JsonNode? this[string name] => _index.TryGetValue(name, out var i) ? _members[i].Value : null;

    public JsonMember? Member(string name) => _index.TryGetValue(name, out var i) ? _members[i] : null;

    /// <summary>Adds a member; a name used twice is an error (the reader reports it with its position).</summary>
    public JsonObject Add(string name, JsonNode value, int line = 0, int column = 0)
    {
        if (!_index.TryAdd(name, _members.Count))
        {
            throw new JsonError(line, column, $"pole „{name}“ je v objekte dvakrát.");
        }

        _members.Add(new JsonMember(name, value, line, column));
        return this;
    }

    /// <summary>Adds the member only when the value is not null (fields left out = their default).</summary>
    public JsonObject AddIf(string name, JsonNode? value) => value is null ? this : Add(name, value);

    public JsonObject AddIf(bool condition, string name, JsonNode value) => condition ? Add(name, value) : this;
}

public sealed class JsonArray(int line = 0, int column = 0) : JsonNode(line, column)
{
    private readonly List<JsonNode> _items = [];

    public override JsonKind Kind => JsonKind.Array;

    public IReadOnlyList<JsonNode> Items => _items;

    public int Count => _items.Count;

    public JsonArray Add(JsonNode item)
    {
        _items.Add(item);
        return this;
    }

    public static JsonArray Of<T>(IEnumerable<T> items, Func<T, JsonNode> node)
    {
        var a = new JsonArray();
        foreach (var item in items)
        {
            a.Add(node(item));
        }

        return a;
    }
}

public sealed class JsonString(string value, int line = 0, int column = 0) : JsonNode(line, column)
{
    public string Value { get; } = value;

    public override JsonKind Kind => JsonKind.String;
}

/// <summary>A number keeps its text exactly as written – nothing is lost before the program says what it wants.</summary>
public sealed class JsonNumber : JsonNode
{
    public JsonNumber(string text, int line = 0, int column = 0)
        : base(line, column) => Text = text;

    public JsonNumber(long value)
        : base(0, 0) => Text = value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A decimal number, written in the shortest form that reads back the same.</summary>
    public JsonNumber(double value)
        : base(0, 0) => Text = double.IsFinite(value)
            ? value.ToString("R", CultureInfo.InvariantCulture)
            : throw new ArgumentOutOfRangeException(nameof(value), "JSON nepozná nekonečno ani NaN.");

    public string Text { get; }

    public override JsonKind Kind => JsonKind.Number;

    /// <summary>A whole number without a fraction or an exponent.</summary>
    public bool TryWhole(out long value) =>
        long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    public double ToDouble() => double.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture);
}

public sealed class JsonBool(bool value, int line = 0, int column = 0) : JsonNode(line, column)
{
    public bool Value { get; } = value;

    public override JsonKind Kind => JsonKind.Bool;
}

public sealed class JsonNull(int line = 0, int column = 0) : JsonNode(line, column)
{
    public override JsonKind Kind => JsonKind.Null;
}

/// <summary>A mistake in a JSON text or in its content: where (line and character from 1; 0 = unknown) and what, in
/// Slovak.</summary>
public sealed class JsonError(int line, int column, string problem) : FormatException(Format(line, column, problem))
{
    public int Line { get; } = line;

    public int Column { get; } = column;

    /// <summary>The message without the position.</summary>
    public string Problem { get; } = problem;

    public static JsonError At(JsonNode node, string problem) => new(node.Line, node.Column, problem);

    private static string Format(int line, int column, string problem) =>
        line > 0 ? $"riadok {line}, znak {column}: {problem}" : problem;
}
