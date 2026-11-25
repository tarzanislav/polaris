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
}
