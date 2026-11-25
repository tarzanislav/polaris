namespace Polaris.Net;

/// <summary>centrum.txt: the address and the pin of a centre on one line ("https://SERVER:8443 PIN"), written by the
/// centre at its installation and laid beside the console and the PC installer – nobody copies 64 characters by hand.</summary>
public static class CentreFile
{
    public const string Name = "centrum.txt";

    /// <summary>Address and pin from the text (a bare address gets https and port 8443), or null.</summary>
    public static (Uri Address, string Pin)? Parse(string? text)
    {
        var parts = (text ?? "").Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var address = parts[0].Contains("://", StringComparison.Ordinal) ? parts[0] : $"https://{parts[0].TrimEnd('/')}:8443";
        var pin = parts[1].ToLowerInvariant();
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0
            && pin.Length == 64 && pin.All(char.IsAsciiHexDigit)
            ? (uri, pin)
            : null;
    }

    public static string Line(Uri address, string pin) => $"{address} {pin}\r\n";

    /// <summary>The consoles a new PC may be admitted by (lines "konzola MENO ODTLAČOK" after the first line): the PC
    /// takes an approval only from one of them.</summary>
    public static List<(string Name, string Fingerprint)> Consoles(string? text)
    {
        var list = new List<(string, string)>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == "konzola" && parts[2].Length == 16 && parts[2].All(char.IsAsciiHexDigit))
            {
                list.Add((parts[1], parts[2].ToUpperInvariant()));
            }
        }

        return list;
    }

    /// <summary>The whole file: the centre, then the consoles (each once).</summary>
    public static string Text(Uri address, string pin, IEnumerable<(string Name, string Fingerprint)> consoles)
    {
        var text = new System.Text.StringBuilder(Line(address, pin));
        foreach (var (name, fingerprint) in consoles.DistinctBy(c => c.Fingerprint.ToUpperInvariant()))
        {
            text.Append($"konzola {name} {fingerprint.ToUpperInvariant()}\r\n");
        }

        return text.ToString();
    }
}
