using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Polaris.Json;

namespace Polaris.Net;

/// <summary>The code an administrator types on a new PC: 60 random bits as twelve letters of an alphabet without
/// look-alikes (no I, L, O, U), shown in three groups. The centre never keeps the code, only its SHA-256.</summary>
public static class RegistrationCode
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 12;

    public static string Generate()
    {
        Span<byte> random = stackalloc byte[Length];
        RandomNumberGenerator.Fill(random);
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[random[i] & 31]; // 32 letters: every byte gives 5 even bits
        }

        var code = new string(chars);
        return $"{code[..4]}-{code[4..8]}-{code[8..]}";
    }

    /// <summary>The code as typed – small letters, spaces, dashes, O for 0 and I or L for 1 forgiven – or null.</summary>
    public static string? Normalize(string typed)
    {
        var sb = new StringBuilder();
        foreach (var c in typed.ToUpperInvariant())
        {
            var d = c switch { 'O' => '0', 'I' or 'L' => '1', 'U' => 'V', _ => c };
            if (d is '-' or ' ')
            {
                continue;
            }

            if (!Alphabet.Contains(d, StringComparison.Ordinal))
            {
                return null;
            }

            sb.Append(d);
        }

        return sb.Length == Length ? sb.ToString() : null;
    }

    public static string Hash(string normalized) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("POLARIS-kod:" + normalized)));
}

/// <summary>A console's permission to register PCs into a group: until when, how many, for which code (its hash). Signed
/// by the console, so a centre checks it on its own and the code alone, without it, opens nothing.</summary>
public sealed record RegistrationGrant(string Group, DateTimeOffset NotAfterUtc, int MaxUses, string CodeHash, string Console, string Signature)
{
    public static readonly TimeSpan Longest = TimeSpan.FromDays(7);
    public const int MostUses = 200;

    private static ReadOnlySpan<byte> Tag => "POLREG01"u8;

    public string Id => CodeHash[..16];

    public static RegistrationGrant Sign(string group, DateTimeOffset notAfterUtc, int maxUses, string codeHash, SigningKey key)
    {
        using var pub = key.Public();
        var unsigned = new RegistrationGrant(group, notAfterUtc.ToUniversalTime(), maxUses, codeHash, pub.Fingerprint(), "");
        return unsigned with { Signature = Convert.ToBase64String(key.Sign(unsigned.Bytes())) };
    }

    /// <summary>The trusted console that signed it, or why it is not valid now.</summary>
    public (TrustedConsole? Console, string Problem) Check(IReadOnlyList<TrustedConsole> trusted, DateTimeOffset now)
    {
        var console = trusted.FirstOrDefault(t => string.Equals(t.Fingerprint, Console, StringComparison.OrdinalIgnoreCase));
        if (console is null)
        {
            return (null, "kód vydala konzola, ktorej centrum nedôveruje");
        }

        try
        {
            using var key = VerifyingKey.FromSubjectPublicKeyInfo(console.PublicKey);
            if (!key.Verify(Bytes(), Convert.FromBase64String(Signature)))
            {
                return (null, "podpis kódu nesedí");
            }
        }
        catch (FormatException)
        {
            return (null, "podpis kódu je poškodený");
        }

        return now > NotAfterUtc ? (null, $"kód platil do {NotAfterUtc.ToLocalTime():d. M. yyyy H:mm}") : (console, "");
    }

    public string ToJson() => JsonText.Canonical(new JsonObject()
        .Add("group", Group)
        .Add("notAfterUtc", NotAfterUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        .Add("maxUses", MaxUses)
        .Add("codeHash", CodeHash)
        .Add("console", Console)
        .Add("signature", Signature));

    public static RegistrationGrant FromJson(string json)
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json, JsonSyntax.Strict), "kód registrácie");
            var grant = new RegistrationGrant(f.String("group"), f.Time("notAfterUtc"), f.Int("maxUses", 1), f.String("codeHash"), f.String("console"), f.String("signature"));
            f.End();
            return grant.Problems() is { } problem ? throw new InvalidDataException(problem) : grant;
        }
        catch (JsonError e)
        {
            throw new InvalidDataException("Kód registrácie sa nedá prečítať: " + e.Message, e);
        }
    }

    /// <summary>What is wrong with its form, or null.</summary>
    public string? Problems() =>
        !PcRules.GroupName(Group) ? "skupina: písmená, číslice, - a _ (do 40 znakov)"
        : MaxUses is < 1 or > MostUses ? $"počet PC: 1 až {MostUses}"
        : CodeHash.Length != 64 || !CodeHash.All(char.IsAsciiHexDigitLower) ? "odtlačok kódu má 64 malých šestnástkových znakov"
        : null;

    private byte[] Bytes() => Framing.Build(Tag, Group, NotAfterUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), MaxUses.ToString(CultureInfo.InvariantCulture), CodeHash, Console);
}

/// <summary>Who a PC is to the centre: its two public keys – one it signs with (reports, requests), one secrets for it are
/// encrypted to (passwords of its administrator, licences – later) – and what it says about itself. Its identifier is
/// taken from its signing key, so nobody can claim another PC's name.</summary>
public sealed record PcIdentity(string SigningKey, string SecretsKey, string Name, string Windows, string Hardware, string Agent)
{
    public string Id => IdOf(SigningKey);

    public static string IdOf(string signingKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(signingKey)).AsSpan(0, 16));

    public JsonObject ToNode() => new JsonObject()
        .Add("signingKey", SigningKey)
        .Add("secretsKey", SecretsKey)
        .Add("name", Name)
        .Add("windows", Windows)
        .Add("hardware", Hardware)
        .Add("agent", Agent);

    public static PcIdentity From(JsonFields f)
    {
        var pc = new PcIdentity(f.String("signingKey"), f.String("secretsKey"), f.String("name"), f.String("windows"), f.String("hardware"), f.String("agent"));
        f.End();
        return pc.Problems() is { } problem ? throw new InvalidDataException(problem) : pc;
    }

    public string? Problems()
    {
        try
        {
            using (VerifyingKey.FromSubjectPublicKeyInfo(Convert.FromBase64String(SigningKey)))
            {
            }

            using var secrets = ECDiffieHellman.Create();
            secrets.ImportSubjectPublicKeyInfo(Convert.FromBase64String(SecretsKey), out _);
            if (secrets.KeySize != 256)
            {
                return "kľúč na tajomstvá PC má byť P-256";
            }
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return "kľúče PC sa nedajú prečítať";
        }

        return Name.Length is < 1 or > 63 || Name.Any(char.IsControl) ? "meno PC: 1 až 63 znakov"
            : Windows.Length > 100 || Hardware.Length > 160 || Agent.Length > 40 ? "popis PC je príliš dlhý"
            : null;
    }
}

/// <summary>A PC asks to be registered: the code as typed, who it is, and its signature over both (it holds the key).</summary>
public sealed record RegistrationRequest(string Code, PcIdentity Pc, string Time, string Proof)
{
    private static ReadOnlySpan<byte> Tag => "POLPCR01"u8;

    public static RegistrationRequest Make(string code, PcIdentity pc, SigningKey key, string serverPin, DateTimeOffset now)
    {
        var time = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var unsigned = new RegistrationRequest(code, pc, time, "");
        return unsigned with { Proof = Convert.ToBase64String(key.Sign(unsigned.Bytes(serverPin))) };
    }

    /// <summary>Signed by the very key it names, for this centre, now.</summary>
    public bool Proven(string serverPin, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(Time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) || (now - at).Duration() > PcSigning.Window)
        {
            return false;
        }

        try
        {
            using var key = VerifyingKey.FromSubjectPublicKeyInfo(Convert.FromBase64String(Pc.SigningKey));
            return key.Verify(Bytes(serverPin), Convert.FromBase64String(Proof));
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public string ToJson() => JsonText.Canonical(new JsonObject()
        .Add("code", Code)
        .Add("pc", Pc.ToNode())
        .Add("time", Time)
        .Add("proof", Proof));

    public static RegistrationRequest FromJson(string json)
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json, JsonSyntax.Strict), "registrácia PC");
            var pc = PcIdentity.From(f.Fields("pc", "PC") ?? throw new InvalidDataException("Registrácii chýba PC."));
            var request = new RegistrationRequest(f.String("code"), pc, f.String("time"), f.String("proof"));
            f.End();
            return request;
        }
        catch (JsonError e)
        {
            throw new InvalidDataException("Registrácia sa nedá prečítať: " + e.Message, e);
        }
    }

    private byte[] Bytes(string serverPin) =>
        Framing.Build(Tag, RegistrationCode.Normalize(Code) ?? Code, JsonText.Canonical(Pc.ToNode()), serverPin.ToLowerInvariant(), Time);
}

/// <summary>A registered PC as the centre keeps it: who it is, its group, when, by which code. The proof of the code
/// (HMAC with the code over the PC) lets the console – the only one that knew the code – confirm the PC later for every
/// centre (two centres, design 4.16), without the code ever being kept.</summary>
public sealed record PcRecord(PcIdentity Pc, string Group, DateTimeOffset RegisteredUtc, string Grant, string CodeProof)
{
    public string Id => Pc.Id;

    public static string ProofOf(string normalizedCode, PcIdentity pc) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode), Encoding.UTF8.GetBytes(JsonText.Canonical(pc.ToNode()))));

    public string ToJson() => JsonText.Canonical(new JsonObject()
        .Add("pc", Pc.ToNode())
        .Add("group", Group)
        .Add("registeredUtc", RegisteredUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        .Add("grant", Grant)
        .Add("codeProof", CodeProof));

    public static PcRecord FromJson(string json)
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json, JsonSyntax.Strict), "záznam PC");
            var pc = PcIdentity.From(f.Fields("pc", "PC") ?? throw new InvalidDataException("Záznamu chýba PC."));
            var record = new PcRecord(pc, f.String("group"), f.Time("registeredUtc"), f.String("grant"), f.String("codeProof"));
            f.End();
            return record;
        }
        catch (JsonError e)
        {
            throw new InvalidDataException("Záznam PC sa nedá prečítať: " + e.Message, e);
        }
    }
}

/// <summary>The envelope of everything a registered PC sends: which PC, when, for which centre, over which bytes – signed
/// with the PC's own key. A stolen code opens nothing after the registration.</summary>
public static class PcSigning
{
    public const string PcHeader = "X-Polaris-Pc";
    public const string TimeHeader = "X-Polaris-Cas";
    public const string SignatureHeader = "X-Polaris-Podpis";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static ReadOnlySpan<byte> Tag => "POLPCS01"u8;

    public static Dictionary<string, string> Headers(byte[] body, string path, string serverPin, string pcId, SigningKey key, DateTimeOffset now)
    {
        var time = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return new Dictionary<string, string>
        {
            [PcHeader] = pcId,
            [TimeHeader] = time,
            [SignatureHeader] = Convert.ToBase64String(key.Sign(Bytes(body, path, serverPin, pcId, time))),
        };
    }

    public static (PcRecord? Pc, string Problem) Verify(Func<string, string?> header, byte[] body, string path, string serverPin, Func<string, PcRecord?> find, DateTimeOffset now)
    {
        if (header(PcHeader) is not { } id || header(TimeHeader) is not { } time || header(SignatureHeader) is not { } signature)
        {
            return (null, "požiadavka nemá podpis PC");
        }

        if (!PcRules.Id(id) || find(id) is not { } record)
        {
            return (null, "PC nie je v centre zaregistrované (polaris-agent registrovat …)");
        }

        if (!DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) || (now - at).Duration() > Window)
        {
            return (null, "čas PC sa líši od času centra o viac ako 10 minút – skontroluj hodiny");
        }

        try
        {
            using var key = VerifyingKey.FromSubjectPublicKeyInfo(Convert.FromBase64String(record.Pc.SigningKey));
            return key.Verify(Bytes(body, path, serverPin, id, time), Convert.FromBase64String(signature)) ? (record, "") : (null, "podpis PC nesedí");
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return (null, "podpis PC je poškodený");
        }
    }

    private static byte[] Bytes(byte[] body, string path, string serverPin, string id, string time) =>
        Framing.Build(Tag, Convert.ToHexStringLower(SHA256.HashData(body)), path, serverPin.ToLowerInvariant(), id, time);
}

/// <summary>What a PC tells the centre about itself (agreed with the owner 24 Nov 2026): technical facts only – never files
/// of pupils, the browser or the screen. The journal and measurements go as the agent's own journal lines.</summary>
public sealed record PcReport(
    DateTimeOffset Utc,
    string Agent,
    string Windows,
    string Hardware,
    string Group,
    string Profile,
    string Recipe,
    long UptimeSeconds,
    long FreeDiskBytes,
    long TotalDiskBytes,
    int DevicesWithoutDriver,
    string Trezor,
    List<string> Journal,
    List<string> Measurements)
{
    public const int MostJournal = 50;
    public const int MostMeasurements = 5;
    public const int Largest = 512 * 1024;

    public string ToJson() => JsonText.Canonical(new JsonObject()
        .Add("utc", Utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        .Add("agent", Agent)
        .Add("windows", Windows)
        .Add("hardware", Hardware)
        .Add("group", Group)
        .Add("profile", Profile)
        .Add("recipe", Recipe)
        .Add("uptimeSeconds", UptimeSeconds)
        .Add("freeDiskBytes", FreeDiskBytes)
        .Add("totalDiskBytes", TotalDiskBytes)
        .Add("devicesWithoutDriver", DevicesWithoutDriver)
        .Add("trezor", Trezor)
        .Add("journal", JsonArray.Of(Journal.TakeLast(MostJournal), l => l))
        .Add("measurements", JsonArray.Of(Measurements.TakeLast(MostMeasurements), l => l)));

    public static PcReport FromJson(string json)
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json, JsonSyntax.Strict), "hlásenie PC");
            var report = new PcReport(
                f.Time("utc"), f.String("agent"), f.String("windows"), f.String("hardware"), f.String("group"), f.String("profile"), f.String("recipe"),
                f.Long("uptimeSeconds", 0, 0), f.Long("freeDiskBytes", 0, 0), f.Long("totalDiskBytes", 0, 0), f.Int("devicesWithoutDriver", 0),
                f.String("trezor"), f.Strings("journal"), f.Strings("measurements"));
            f.End();
            return report.Journal.Count > MostJournal || report.Measurements.Count > MostMeasurements
                ? throw new InvalidDataException("Hlásenie má viac záznamov, ako sa posiela.")
                : report;
        }
        catch (JsonError e)
        {
            throw new InvalidDataException("Hlásenie sa nedá prečítať: " + e.Message, e);
        }
    }
}

public static class PcRules
{
    public static bool GroupName(string name) =>
        name.Length is > 0 and <= 40 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The identifier of a PC: 32 small hexadecimal characters.</summary>
    public static bool Id(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);
}

/// <summary>The paths of the centre a PC and a console use (one place, both sides).</summary>
public static class CentrePaths
{
    public const string Codes = "/v1/kody";
    public const string Register = "/v1/registracia";
    public const string Report = "/v1/hlasenie";
    public const string Pcs = "/v1/pc";
}
