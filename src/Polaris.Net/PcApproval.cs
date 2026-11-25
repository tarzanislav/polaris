using System.Globalization;
using System.Security.Cryptography;
using Polaris.Json;

namespace Polaris.Net;

/// <summary>A PC asks to be admitted without a code (decided 25. 11. 2026): who it is, signed by its own key for this
/// centre now. The centre keeps it as waiting until a console approves it.</summary>
public sealed record PcApplication(PcIdentity Pc, string Time, string Proof)
{
    private static ReadOnlySpan<byte> Tag => "POLPCA01"u8;

    /// <summary>The short code both the PC's window and the console show, to see that it is this PC.</summary>
    public static string CheckCode(string pcId) => (pcId[..4] + "-" + pcId[4..8]).ToUpperInvariant();

    public static PcApplication Make(PcIdentity pc, SigningKey key, string serverPin, DateTimeOffset now)
    {
        var time = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var unsigned = new PcApplication(pc, time, "");
        return unsigned with { Proof = Convert.ToBase64String(key.Sign(unsigned.Bytes(serverPin))) };
    }

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

    public string ToJson() => JsonText.Canonical(new JsonObject().Add("pc", Pc.ToNode()).Add("time", Time).Add("proof", Proof));

    public static PcApplication FromJson(string json)
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json, JsonSyntax.Strict), "prihláška PC");
            var pc = PcIdentity.From(f.Fields("pc", "PC") ?? throw new InvalidDataException("Prihláške chýba PC."));
            var application = new PcApplication(pc, f.String("time"), f.String("proof"));
            f.End();
            return application;
        }
        catch (JsonError e)
        {
            throw new InvalidDataException("Prihláška sa nedá prečítať: " + e.Message, e);
        }
    }

    private byte[] Bytes(string serverPin) => Framing.Build(Tag, JsonText.Canonical(Pc.ToNode()), serverPin.ToLowerInvariant(), Time);
}

/// <summary>A console admits one PC into a group: the PC (its identifier), the group, the consoles the PC is to trust and
/// for which centre – signed by the console. The PC takes it only from a console it knows from centrum.txt (the file the
/// administrator brought); the centre only passes it on.</summary>
public sealed record PcApproval(string Pc, string Group, string Trust, string Time, string Console, string ConsoleKey, string Signature)
{
    private static ReadOnlySpan<byte> Tag => "POLAPR01"u8;

    public static PcApproval Sign(string pcId, string group, IEnumerable<TrustedConsole> trust, string serverPin, SigningKey key, string console, DateTimeOffset now)
    {
        using var pub = key.Public();
        var unsigned = new PcApproval(pcId, group, TrustList.ToJson(trust), now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), console, Convert.ToBase64String(pub.Export()), "");
        return unsigned with { Signature = Convert.ToBase64String(key.Sign(unsigned.Bytes(serverPin))) };
    }

    /// <summary>The fingerprint of the console that signed (as consoles are known everywhere).</summary>
    public string ConsoleFingerprint()
    {
        try
        {
            using var key = VerifyingKey.FromSubjectPublicKeyInfo(Convert.FromBase64String(ConsoleKey));
            return key.Fingerprint();
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return "";
        }
    }

    /// <summary>Whether a console with one of these fingerprints signed it for this centre and this PC; the consoles to
    /// trust, or the reason not.</summary>
    public (List<TrustedConsole>? Trust, string Problem) Check(string pcId, string serverPin, IEnumerable<string> knownConsoles)
    {
        var fingerprint = ConsoleFingerprint();
        if (fingerprint.Length == 0 || !knownConsoles.Contains(fingerprint, StringComparer.OrdinalIgnoreCase))
        {
            return (null, $"schválenie podpísala konzola {Console}, ktorú toto PC nepozná (nie je v centrum.txt)");
        }

        if (Pc != pcId || !PcRules.GroupName(Group))
        {
            return (null, "schválenie je pre iné PC alebo nemá platnú skupinu");
        }

        try
        {
            using var key = VerifyingKey.FromSubjectPublicKeyInfo(Convert.FromBase64String(ConsoleKey));
            if (!key.Verify(Bytes(serverPin), Convert.FromBase64String(Signature)))
            {
                return (null, "podpis schválenia nesedí – je podvrhnuté alebo pre iné centrum");
            }

            var trust = TrustList.FromJson(Trust);
            return trust.Any(t => string.Equals(t.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                ? (trust, "")
                : (null, "schválenie neobsahuje konzolu, ktorá ho podpísala");
        }
        catch (Exception e) when (e is FormatException or CryptographicException or InvalidDataException)
        {
            return (null, "schválenie je poškodené: " + e.Message);
        }
    }

    public string ToJson() => JsonText.Canonical(new JsonObject()
        .Add("pc", Pc)
        .Add("group", Group)
        .Add("trust", Trust)
        .Add("time", Time)
        .Add("console", Console)
        .Add("consoleKey", ConsoleKey)
        .Add("signature", Signature));

    public static PcApproval FromJson(string json)
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json, JsonSyntax.Strict), "schválenie PC");
            var approval = new PcApproval(f.String("pc"), f.String("group"), f.String("trust"), f.String("time"), f.String("console"), f.String("consoleKey"), f.String("signature"));
            f.End();
            return approval;
        }
        catch (JsonError e)
        {
            throw new InvalidDataException("Schválenie sa nedá prečítať: " + e.Message, e);
        }
    }

    private byte[] Bytes(string serverPin) => Framing.Build(
        Tag, Pc, Group, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Trust))), serverPin.ToLowerInvariant(), Time, ConsoleKey);
}
