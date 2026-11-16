using System.Text;

namespace Polaris.Json;

/// <summary>What a reader allows. Files written by people (recipes) may have comments and a comma after the last item;
/// messages between programs may not. Depth and length are limited always – a text can never exhaust the reader.</summary>
public sealed record JsonSyntax(bool Comments, bool TrailingCommas, int MaxDepth, int MaxLength)
{
    /// <summary>Files written by people: // and /* */ comments, a comma after the last item, up to 8 MB.</summary>
    public static readonly JsonSyntax Human = new(true, true, 64, 8 * 1024 * 1024);

    /// <summary>Messages and records written by programs: pure JSON (RFC 8259), up to 1 MB.</summary>
    public static readonly JsonSyntax Strict = new(false, false, 32, 1024 * 1024);
}

/// <summary>Reads a JSON text into a tree of <see cref="JsonNode"/>. Every mistake gives a <see cref="JsonError"/> with
/// the line and the character. Also refused: a name twice in one object, a broken escape, a lonely half of a surrogate
/// pair, control characters inside a text, anything after the value.</summary>
public sealed class JsonReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _text;
    private readonly JsonSyntax _syntax;
    private readonly StringBuilder _buffer = new();
    private int _pos;
    private int _line = 1;
    private int _lineStart;
    private int _depth;

    private JsonReader(string text, JsonSyntax syntax)
    {
        _text = text;
        _syntax = syntax;
    }

    public static JsonNode Parse(string text, JsonSyntax? syntax = null)
    {
        syntax ??= JsonSyntax.Strict;
        if (text.Length > syntax.MaxLength)
        {
            throw new JsonError(0, 0, $"text je dlhší ako {syntax.MaxLength} znakov.");
        }

        var reader = new JsonReader(text, syntax);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            reader._pos = 1;
            reader._lineStart = 1;
        }

        reader.SkipSpace();
        var value = reader.Value();
        reader.SkipSpace();
        if (reader._pos < text.Length)
        {
            throw reader.Error("za koncom hodnoty je ešte ďalší text.");
        }

        return value;
    }

    /// <summary>Reads UTF-8 bytes; bytes that are not valid UTF-8 are a mistake, never silently replaced.</summary>
    public static JsonNode Parse(ReadOnlySpan<byte> utf8, JsonSyntax? syntax = null)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            throw new JsonError(0, 0, "súbor nie je v kódovaní UTF-8.");
        }

        return Parse(text, syntax);
    }

    public static JsonNode ParseFile(string path, JsonSyntax? syntax = null)
    {
        var limit = (syntax ?? JsonSyntax.Strict).MaxLength;
        var info = new FileInfo(path);
        if (info.Exists && info.Length > (long)limit * 3)
        {
            throw new JsonError(0, 0, $"súbor je väčší ako {limit} znakov.");
        }

        return Parse(File.ReadAllBytes(path), syntax);
    }

    private int Column => _pos - _lineStart + 1;

    private JsonError Error(string problem) => new(_line, Column, problem);

    private JsonNode Value()
    {
        if (_pos >= _text.Length)
        {
            throw Error("text sa skončil, chýba hodnota.");
        }

        var (line, column) = (_line, Column);
        switch (_text[_pos])
        {
            case '{':
                return Object(line, column);
            case '[':
                return Array(line, column);
            case '"':
                return new JsonString(String(), line, column);
            case 't':
                Word("true");
                return new JsonBool(true, line, column);
            case 'f':
                Word("false");
                return new JsonBool(false, line, column);
            case 'n':
                Word("null");
                return new JsonNull(line, column);
            case '-' or (>= '0' and <= '9'):
                return new JsonNumber(Number(), line, column);
            case '\'':
                throw Error("text sa píše v úvodzovkách \"…\", nie v apostrofoch.");
            default:
                throw Error($"tu má byť hodnota (text v úvodzovkách, číslo, true, false, null, {{ alebo [), je tu „{Shown(_text[_pos])}“.");
        }
    }

    private JsonObject Object(int line, int column)
    {
        Enter();
        var o = new JsonObject(line, column);
        _pos++;
        SkipSpace();
        if (Peek() == '}')
        {
            _pos++;
            _depth--;
            return o;
        }

        while (true)
        {
            SkipSpace();
            if (Peek() != '"')
            {
                throw Peek() == '}' && o.Count > 0
                    ? Error("za poslednou položkou nesmie byť čiarka.")
                    : Error("tu má byť meno poľa v úvodzovkách.");
            }

            var (nameLine, nameColumn) = (_line, Column);
            var name = String();
            SkipSpace();
            if (Peek() != ':')
            {
                throw Error($"za menom poľa „{name}“ chýba dvojbodka.");
            }

            _pos++;
            SkipSpace();
            var value = Value();
            if (o.Member(name) is { } earlier)
            {
                throw new JsonError(nameLine, nameColumn, $"pole „{name}“ je v objekte dvakrát (prvýkrát na riadku {earlier.Line}).");
            }

            o.Add(name, value, nameLine, nameColumn);
            SkipSpace();
            switch (Peek())
            {
                case ',':
                    _pos++;
                    SkipSpace();
                    if (Peek() == '}' && _syntax.TrailingCommas)
                    {
                        _pos++;
                        _depth--;
                        return o;
                    }

                    continue;
                case '}':
                    _pos++;
                    _depth--;
                    return o;
                default:
                    throw _pos >= _text.Length
                        ? new JsonError(line, column, "objekt { nie je uzavretý }.")
                        : Error("medzi poľami chýba čiarka, alebo objekt nie je uzavretý }.");
            }
        }
    }

    private JsonArray Array(int line, int column)
    {
        Enter();
        var a = new JsonArray(line, column);
        _pos++;
        SkipSpace();
        if (Peek() == ']')
        {
            _pos++;
            _depth--;
            return a;
        }

        while (true)
        {
            SkipSpace();
            if (Peek() == ']')
            {
                throw Error("za poslednou položkou nesmie byť čiarka.");
            }

            a.Add(Value());
            SkipSpace();
            switch (Peek())
            {
                case ',':
                    _pos++;
                    SkipSpace();
                    if (Peek() == ']' && _syntax.TrailingCommas)
                    {
                        _pos++;
                        _depth--;
                        return a;
                    }

                    continue;
                case ']':
                    _pos++;
                    _depth--;
                    return a;
                default:
                    throw _pos >= _text.Length
                        ? new JsonError(line, column, "zoznam [ nie je uzavretý ].")
                        : Error("medzi položkami chýba čiarka, alebo zoznam nie je uzavretý ].");
            }
        }
    }

    private void Enter()
    {
        if (++_depth > _syntax.MaxDepth)
        {
            throw Error($"príliš veľa vnorených úrovní (najviac {_syntax.MaxDepth}).");
        }
    }

    private string String()
    {
        var (line, column) = (_line, Column);
        _pos++;
        var start = _pos;

        // the common case: no escapes – the text is cut out without copying character by character
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c == '"')
            {
                return _text[start.._pos++];
            }

            if (c == '\\' || c < ' ' || char.IsSurrogate(c))
            {
                break;
            }

            _pos++;
        }

        _buffer.Clear().Append(_text, start, _pos - start);
        while (true)
        {
            if (_pos >= _text.Length)
            {
                throw new JsonError(line, column, "text nie je uzavretý úvodzovkami.");
            }

            var c = _text[_pos];
            if (c == '"')
            {
                _pos++;
                return _buffer.ToString();
            }

            if (c < ' ')
            {
                throw Error(c == '\n' ? "text nie je uzavretý úvodzovkami na tom istom riadku (nový riadok sa píše \\n)." : "riadiaci znak v texte sa musí zapísať ako \\u00XX.");
            }

            if (char.IsHighSurrogate(c) && _pos + 1 < _text.Length && char.IsLowSurrogate(_text[_pos + 1]))
            {
                _buffer.Append(c).Append(_text[_pos + 1]);
                _pos += 2;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                throw Error("neplatný znak (osamotená polovica dvojice UTF-16).");
            }

            if (c != '\\')
            {
                _buffer.Append(c);
                _pos++;
                continue;
            }

            _pos++;
            var e = Peek();
            _pos++;
            switch (e)
            {
                case '"': _buffer.Append('"'); break;
                case '\\': _buffer.Append('\\'); break;
                case '/': _buffer.Append('/'); break;
                case 'b': _buffer.Append('\b'); break;
                case 'f': _buffer.Append('\f'); break;
                case 'n': _buffer.Append('\n'); break;
                case 'r': _buffer.Append('\r'); break;
                case 't': _buffer.Append('\t'); break;
                case 'u':
                    var unit = Hex4();
                    if (char.IsHighSurrogate(unit))
                    {
                        if (Peek() != '\\' || _pos + 1 >= _text.Length || _text[_pos + 1] != 'u')
                        {
                            throw Error("po \\u s prvou polovicou dvojice UTF-16 musí nasledovať druhá.");
                        }

                        _pos += 2;
                        var low = Hex4();
                        if (!char.IsLowSurrogate(low))
                        {
                            throw Error("druhá polovica dvojice UTF-16 je neplatná.");
                        }

                        _buffer.Append(unit).Append(low);
                    }
                    else if (char.IsLowSurrogate(unit))
                    {
                        throw Error("osamotená druhá polovica dvojice UTF-16.");
                    }
                    else
                    {
                        _buffer.Append(unit);
                    }

                    break;
                default:
                    _pos--;
                    throw Error(e == '\0' ? "text sa skončil uprostred \\." : $"neznáma skratka \\{Shown(e)} (cesty Windows píš s dvojitým \\\\, napr. C:\\\\Users).");
            }
        }
    }

    private char Hex4()
    {
        if (_pos + 4 > _text.Length)
        {
            throw Error("\\u potrebuje 4 šestnástkové číslice.");
        }

        var v = 0;
        for (var i = 0; i < 4; i++)
        {
            var d = HexDigit(_text[_pos + i]);
            if (d < 0)
            {
                throw Error("\\u potrebuje 4 šestnástkové číslice.");
            }

            v = (v << 4) | d;
        }

        _pos += 4;
        return (char)v;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>The grammar of RFC 8259: -?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?</summary>
    private string Number()
    {
        var start = _pos;
        if (Peek() == '-')
        {
            _pos++;
        }

        if (Peek() == '0')
        {
            _pos++;
            if (char.IsAsciiDigit(Peek()))
            {
                throw Error("číslo nesmie začínať nulou (01); ak ide o kód, napíš ho ako text v úvodzovkách.");
            }
        }
        else if (!Digits())
        {
            throw Error("za znamienkom - chýba číslo.");
        }

        if (Peek() == '.')
        {
            _pos++;
            if (!Digits())
            {
                throw Error("za desatinnou bodkou chýba číslica.");
            }
        }

        if (Peek() is 'e' or 'E')
        {
            _pos++;
            if (Peek() is '+' or '-')
            {
                _pos++;
            }

            if (!Digits())
            {
                throw Error("v exponente chýba číslica.");
            }
        }

        if (char.IsAsciiLetter(Peek()) || Peek() == '_')
        {
            throw Error("za číslom sú písmená; ak ide o text, daj ho do úvodzoviek.");
        }

        return _text[start.._pos];
    }

    private bool Digits()
    {
        var start = _pos;
        while (char.IsAsciiDigit(Peek()))
        {
            _pos++;
        }

        return _pos > start;
    }

    private void Word(string word)
    {
        if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0 || char.IsAsciiLetterOrDigit(At(_pos + word.Length)))
        {
            throw Error($"neznáme slovo – myslel si {word}? (text sa píše v úvodzovkách).");
        }

        _pos += word.Length;
    }

    private void SkipSpace()
    {
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            switch (c)
            {
                case ' ' or '\t' or '\r':
                    _pos++;
                    break;
                case '\n':
                    NewLine();
                    break;
                case '/' when _syntax.Comments && At(_pos + 1) == '/':
                    while (_pos < _text.Length && _text[_pos] != '\n')
                    {
                        _pos++;
                    }

                    break;
                case '/' when _syntax.Comments && At(_pos + 1) == '*':
                    var (line, column) = (_line, Column);
                    _pos += 2;
                    while (!(At(_pos) == '*' && At(_pos + 1) == '/'))
                    {
                        if (_pos >= _text.Length)
                        {
                            throw new JsonError(line, column, "komentár /* nie je uzavretý */.");
                        }

                        if (_text[_pos] == '\n')
                        {
                            NewLine();
                        }
                        else
                        {
                            _pos++;
                        }
                    }

                    _pos += 2;
                    break;
                case '/':
                    throw Error("komentáre tu nie sú dovolené.");
                default:
                    return;
            }
        }
    }

    private void NewLine()
    {
        _pos++;
        _line++;
        _lineStart = _pos;
    }

    private char Peek() => At(_pos);

    private char At(int i) => i < _text.Length ? _text[i] : '\0';

    private static string Shown(char c) => c < ' ' ? $"\\u{(int)c:x4}" : c.ToString();
}
