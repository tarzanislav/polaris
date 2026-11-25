using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Polaris.Json;

namespace Polaris.Net;

/// <summary>What POLARIS updates through the centre: the client of the PCs and the servers. The console updates itself
/// from GitHub (the person at it clicks) and hands the others on – signed by its key.</summary>
public static class UpdateProducts
{
    public const string Client = "klient";
    public const string ServerDebian = "server-debian";
    public const string ServerWindows = "server-windows";

    public static readonly IReadOnlyList<string> All = [Client, ServerDebian, ServerWindows];

    /// <summary>The file of each product in a release on GitHub.</summary>
    public static string? ReleaseFile(string product) => product switch
    {
        Client => "POLARIS-Klient-pre-PC-ucebne.exe",
        ServerDebian => "POLARIS-Server-Debian.deb",
        ServerWindows => "POLARIS-Server-Windows.exe",
        _ => null,
    };

    public const string ConsoleFile = "POLARIS-Konzola-spravcu.exe";

    public static string Title(string product) => product switch
    {
        Client => "klient na PC učebne",
        ServerDebian => "server na Debiane",
        ServerWindows => "server na Windows",
        _ => product,
    };
}

/// <summary>A new version of a product, as a console approved it: which file (its SHA-256 and size), which version,
/// signed by the console. A PC or a server takes it only when the signature is of a console it trusts and the file is
/// exactly this one – the centre only passes it on and cannot change a byte unnoticed.</summary>
public sealed record UpdateManifest(string Product, string Version, string Sha256, long Size, DateTimeOffset SignedUtc, string Console, string ConsoleKey, string Signature)
{
    public string ToJson() => JsonText.Readable(new JsonObject()
        .Add("product", Product)
        .Add("version", Version)
        .Add("sha256", Sha256)
        .Add("size", Size)
        .Add("signedUtc", SignedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        .Add("console", Console)
        .Add("consoleKey", ConsoleKey)
        .Add("signature", Signature));

    public static UpdateManifest FromJson(string json)
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json, JsonSyntax.Strict), "aktualizácia");
            var m = new UpdateManifest(
                f.String("product"),
                f.String("version"),
                f.String("sha256"),
                f.Long("size", 0),
                DateTimeOffset.Parse(f.String("signedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                f.String("console"),
                f.String("consoleKey"),
                f.String("signature"));
            f.End();
            return m;
        }
        catch (Exception e) when (e is JsonError or FormatException)
        {
            throw new InvalidDataException("Popis aktualizácie sa nedá prečítať: " + e.Message, e);
        }
    }
}

public static partial class UpdateSigning
{
    public const long MostBytes = 64L * 1024 * 1024;
    private static ReadOnlySpan<byte> Tag => "POLUPD01"u8;

    public static UpdateManifest Sign(string product, string version, byte[] file, SigningKey key, string console, DateTimeOffset now)
    {
        using var pub = key.Public();
        var unsigned = new UpdateManifest(product, version, Convert.ToHexStringLower(SHA256.HashData(file)), file.LongLength, now.ToUniversalTime(), console, pub.Fingerprint(), "");
        return unsigned with { Signature = Convert.ToBase64String(key.Sign(Bytes(unsigned))) };
    }

    /// <summary>Whether the manifest is whole, for a product POLARIS knows, signed by a trusted console.</summary>
    public static SignatureCheck Check(UpdateManifest m, IReadOnlyList<TrustedConsole> trusted)
    {
        if (!UpdateProducts.All.Contains(m.Product) || !Version.IsMatch(m.Version) || !Sha256.IsMatch(m.Sha256) || m.Size is <= 0 or > MostBytes)
        {
            return new SignatureCheck(SignatureState.ContentChanged, "popis aktualizácie nie je v správnom tvare");
        }

        var console = trusted.FirstOrDefault(t => string.Equals(t.Fingerprint, m.ConsoleKey, StringComparison.OrdinalIgnoreCase));
        if (console is null)
        {
            return new SignatureCheck(SignatureState.UnknownKey, $"aktualizáciu podpísala konzola {m.Console}, ktorej sa nedôveruje");
        }

        try
        {
            using var key = VerifyingKey.FromSubjectPublicKeyInfo(console.PublicKey);
            return key.Fingerprint() == console.Fingerprint && key.Verify(Bytes(m), Convert.FromBase64String(m.Signature))
                ? new SignatureCheck(SignatureState.Valid, $"aktualizáciu {m.Version} schválila konzola {console.Name}")
                : new SignatureCheck(SignatureState.Broken, "podpis aktualizácie nesedí – je podvrhnutá alebo poškodená");
        }
        catch (FormatException)
        {
            return new SignatureCheck(SignatureState.Broken, "podpis aktualizácie je poškodený");
        }
    }

    /// <summary>Whether the bytes are exactly the file the manifest names.</summary>
    public static bool Matches(UpdateManifest m, Stream file)
    {
        if (file.Length != m.Size)
        {
            return false;
        }

        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(file)), m.Sha256, StringComparison.Ordinal);
    }

    /// <summary>Compares versions like 0.1.3 (a suffix such as ~ci90 sorts before the plain version). Below zero: a is older.</summary>
    public static int Compare(string a, string b)
    {
        var (na, sa) = Split(a);
        var (nb, sb) = Split(b);
        var c = na.CompareTo(nb);
        if (c != 0)
        {
            return c;
        }

        // 0.1.3~ci5 < 0.1.3 (a build before the release)
        return (sa.Length == 0, sb.Length == 0) switch
        {
            (true, true) => 0,
            (true, false) => 1,
            (false, true) => -1,
            _ => string.CompareOrdinal(sa, sb),
        };
    }

    public static bool Newer(string candidate, string current) => Compare(candidate, current) > 0;

    private static (System.Version Number, string Suffix) Split(string v)
    {
        var text = v.TrimStart('v');
        var cut = text.IndexOfAny(['~', '-', '+']);
        var number = cut < 0 ? text : text[..cut];
        return (System.Version.TryParse(number.Count(c => c == '.') == 0 ? number + ".0" : number, out var parsed) ? parsed : new System.Version(0, 0), cut < 0 ? "" : text[cut..]);
    }

    private static byte[] Bytes(UpdateManifest m) => Framing.Build(
        Tag,
        m.Product,
        m.Version,
        m.Sha256.ToLowerInvariant(),
        m.Size.ToString(CultureInfo.InvariantCulture),
        m.SignedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        m.Console,
        m.ConsoleKey);

    [GeneratedRegex("^[0-9]+(\\.[0-9]+){1,3}([~+-][0-9A-Za-z.~+-]{1,40})?$")]
    private static partial Regex VersionPattern();

    private static Regex Version => VersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    private static Regex Sha256 => Sha256Pattern();

    /// <summary>The paths on the centre: the manifest of a product and the file by its SHA-256.</summary>
    public static string ManifestPath(string product) => $"/v1/aktualizacie/{product}.json";

    public static string FilePath(string sha256) => $"/v1/aktualizacie/{sha256}.bin";

    public const string UploadPath = "/v1/aktualizacia";
    public const string ManifestHeader = "X-Polaris-Aktualizacia";
}

/// <summary>The updates a centre passes on (in its publication, folder aktualizacie): per product the manifest a console
/// signed and the one file it names. A newer version replaces the older; nothing unsigned or different gets in.</summary>
public sealed class UpdateFolder(string root)
{
    private static readonly Lock Gate = new();

    public string Root { get; } = root;

    public string ManifestFile(string product) => Path.Combine(Root, product + ".json");

    public string FileOf(string sha256) => Path.Combine(Root, sha256 + ".bin");

    public UpdateManifest? Manifest(string product)
    {
        try
        {
            return UpdateProducts.All.Contains(product) && File.Exists(ManifestFile(product)) ? UpdateManifest.FromJson(File.ReadAllText(ManifestFile(product))) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Takes a new version from a console: whether it was taken, and a line for people.</summary>
    public (bool Ok, string Message) Accept(UpdateManifest m, byte[] file, IReadOnlyList<TrustedConsole> trusted)
    {
        if (UpdateSigning.Check(m, trusted) is { State: not SignatureState.Valid } bad)
        {
            return (false, bad.Message);
        }

        using (var bytes = new MemoryStream(file, writable: false))
        {
            if (!UpdateSigning.Matches(m, bytes))
            {
                return (false, "súbor nie je ten, ktorý konzola podpísala (iná veľkosť alebo odtlačok)");
            }
        }

        lock (Gate)
        {
            if (Manifest(m.Product) is { } current && UpdateSigning.Compare(current.Version, m.Version) > 0)
            {
                return (false, $"centrum už má novšiu verziu {current.Version} ({UpdateProducts.Title(m.Product)})");
            }

            Directory.CreateDirectory(Root);
            Write(FileOf(m.Sha256), file);
            Write(ManifestFile(m.Product), System.Text.Encoding.UTF8.GetBytes(m.ToJson()));

            // files no manifest names any more go away
            var kept = UpdateProducts.All.Select(Manifest).OfType<UpdateManifest>().Select(x => x.Sha256 + ".bin").ToHashSet(StringComparer.Ordinal);
            foreach (var old in Directory.GetFiles(Root, "*.bin").Where(f => !kept.Contains(Path.GetFileName(f))))
            {
                File.Delete(old);
            }

            return (true, $"{UpdateProducts.Title(m.Product)} {m.Version}: pripravené na aktualizáciu");
        }
    }

    private static void Write(string path, byte[] bytes)
    {
        File.WriteAllBytes(path + ".novy", bytes);
        File.Move(path + ".novy", path, overwrite: true);
    }
}
