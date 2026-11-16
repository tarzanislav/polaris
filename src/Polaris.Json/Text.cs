using System.Security.Cryptography;
using System.Text;

namespace Polaris.Json;

/// <summary>Writing a tree as text. Two forms:
/// <list type="bullet">
/// <item><b>Canonical</b> – one exact form, the basis of fingerprints and signatures. No spaces, members in the order of
/// the tree, numbers as they are, texts in UTF-8 with only the escapes JSON requires (<c>\"</c>, <c>\\</c> and control
/// characters: <c>\b \t \n \f \r</c>, others <c>\u00xx</c>). It never depends on a library or a version of .NET.</item>
/// <item><b>Readable</b> – for people: two spaces of indentation, short lists and simple objects on one line.</item>
/// </list></summary>
public static class JsonText
{
    private const int LineWidth = 100;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Canonical(JsonNode node)
    {
        var sb = new StringBuilder();
        WriteCompact(sb, node, spaced: false);
        return sb.ToString();
    }

    public static byte[] CanonicalUtf8(JsonNode node) => Utf8.GetBytes(Canonical(node));

    /// <summary>SHA-256 of the canonical form, small hex letters.</summary>
    public static string Fingerprint(JsonNode node) => Convert.ToHexStringLower(SHA256.HashData(CanonicalUtf8(node)));

    public static string Readable(JsonNode node)
    {
        var sb = new StringBuilder();
        WriteReadable(sb, node, 0, 0);
        return sb.Append('\n').ToString();
    }

    private static void WriteCompact(StringBuilder sb, JsonNode node, bool spaced)
    {
        switch (node)
        {
            case JsonObject o:
                if (o.Count == 0)
                {
                    sb.Append("{}");
                    break;
                }

                sb.Append(spaced ? "{ " : "{");
                for (var i = 0; i < o.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(spaced ? ", " : ",");
                    }

                    Quote(sb, o.Members[i].Name);
                    sb.Append(spaced ? ": " : ":");
                    WriteCompact(sb, o.Members[i].Value, spaced);
                }

                sb.Append(spaced ? " }" : "}");
                break;
            case JsonArray a:
                if (a.Count == 0)
                {
                    sb.Append("[]");
                    break;
                }

                sb.Append(spaced ? "[ " : "[");
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(spaced ? ", " : ",");
                    }

                    WriteCompact(sb, a.Items[i], spaced);
                }

                sb.Append(spaced ? " ]" : "]");
                break;
            default:
                Scalar(sb, node);
                break;
        }
    }

    private static void WriteReadable(StringBuilder sb, JsonNode node, int indent, int column)
    {
        if (node is not (JsonObject or JsonArray))
        {
            Scalar(sb, node);
            return;
        }

        // a container with only simple values inside goes on one line when it fits
        if (Flat(node))
        {
            var start = sb.Length;
            WriteCompact(sb, node, spaced: true);
            if (column + sb.Length - start <= LineWidth)
            {
                return;
            }

            sb.Length = start;
        }

        var inner = indent + 2;
        if (node is JsonObject o)
        {
            sb.Append('{');
            for (var i = 0; i < o.Count; i++)
            {
                sb.Append(i > 0 ? ",\n" : "\n").Append(' ', inner);
                var lineStart = sb.Length - inner;
                Quote(sb, o.Members[i].Name);
                sb.Append(": ");
                WriteReadable(sb, o.Members[i].Value, inner, sb.Length - lineStart);
            }

            sb.Append('\n').Append(' ', indent).Append('}');
        }
        else
        {
            var a = (JsonArray)node;
            sb.Append('[');
            for (var i = 0; i < a.Count; i++)
            {
                sb.Append(i > 0 ? ",\n" : "\n").Append(' ', inner);
                WriteReadable(sb, a.Items[i], inner, inner);
            }

            sb.Append('\n').Append(' ', indent).Append(']');
        }
    }

    /// <summary>Empty, or holding only simple values and lists of simple values.</summary>
    private static bool Flat(JsonNode node) => node switch
    {
        JsonObject o => o.Members.All(m => m.Value is not JsonObject && (m.Value is not JsonArray a || a.Items.All(i => i is not (JsonObject or JsonArray)))),
        JsonArray a => a.Items.All(i => i is not (JsonObject or JsonArray)),
        _ => true,
    };

    private static void Scalar(StringBuilder sb, JsonNode node)
    {
        switch (node)
        {
            case JsonString s:
                Quote(sb, s.Value);
                break;
            case JsonNumber n:
                sb.Append(n.Text);
                break;
            case JsonBool b:
                sb.Append(b.Value ? "true" : "false");
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    /// <summary>A text in quotes with only the escapes JSON requires; a broken UTF-16 text cannot be written.</summary>
    public static void Quote(StringBuilder sb, string text)
    {
        sb.Append('"');
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c >= ' ' && c != '"' && c != '\\' && !char.IsSurrogate(c))
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                throw new ArgumentException("Text obsahuje osamotenú polovicu dvojice UTF-16 – nedá sa zapísať do JSON.", nameof(text));
            }

            sb.Append(text, start, i - start);
            start = i + 1;
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append("\\u00").Append(HexLower[c >> 4]).Append(HexLower[c & 0xF]); break;
            }
        }

        sb.Append(text, start, text.Length - start).Append('"');
    }

    private const string HexLower = "0123456789abcdef";
}
