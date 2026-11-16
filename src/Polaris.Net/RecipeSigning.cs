using System.Globalization;
using System.Security.Cryptography;
using Polaris.Json;
using Polaris.Recipes;

namespace Polaris.Net;

/// <summary>The signature of a recipe, kept next to it as "recept.json.podpis". It signs the fingerprint of the recipe,
/// so any change of the recipe breaks it.</summary>
public sealed class RecipeSignature
{
    public int Schema { get; set; } = 1;

    public string Recipe { get; set; } = "";

    public int Version { get; set; }

    /// <summary>SHA-256 of the recipe (<see cref="RecipeText.Fingerprint"/>).</summary>
    public string Fingerprint { get; set; } = "";

    public DateTimeOffset SignedUtc { get; set; }

    /// <summary>Name of the console that signed.</summary>
    public string Console { get; set; } = "";

    /// <summary>Fingerprint of the console's public key.</summary>
    public string ConsoleKey { get; set; } = "";

    /// <summary>ECDSA P-256 signature, Base64.</summary>
    public string Signature { get; set; } = "";
}

/// <summary>A key file of a console: the public file has only the public key, the private file also the private key
/// encrypted by the admin's password.</summary>
public sealed class ConsoleKeyFile
{
    public int Schema { get; set; } = 1;

    public string Name { get; set; } = "";

    public string Fingerprint { get; set; } = "";

    /// <summary>Public key (SubjectPublicKeyInfo), Base64.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>Private key, PKCS#8 encrypted by a password, Base64 – only in the private file.</summary>
    public string? EncryptedPrivateKey { get; set; }
}

/// <summary>A console the agent trusts: its name and public key.</summary>
public sealed record TrustedConsole(string Name, string Fingerprint, byte[] PublicKey);

public enum SignatureState
{
    /// <summary>No signature file.</summary>
    Unsigned,

    /// <summary>Signed by a trusted console and the recipe is unchanged.</summary>
    Valid,

    /// <summary>The recipe changed after it was signed.</summary>
    ContentChanged,

    /// <summary>Signed by a console this PC does not trust.</summary>
    UnknownKey,

    /// <summary>The signature does not fit (damaged or forged).</summary>
    Broken,
}

public sealed record SignatureCheck(SignatureState State, string Message);

public static class RecipeSigning
{
    public const string Extension = ".podpis";
    private static readonly byte[] Tag = "POLRCP01"u8.ToArray();

    public static string SignatureFileOf(string recipeFile) => recipeFile + Extension;

    public static RecipeSignature Sign(Recipe recipe, SigningKey key, string console, DateTimeOffset now)
    {
        using var pub = key.Public();
        var signature = new RecipeSignature
        {
            Recipe = recipe.Name,
            Version = recipe.Version,
            Fingerprint = RecipeText.Fingerprint(recipe),
            SignedUtc = now.ToUniversalTime(),
            Console = console,
            ConsoleKey = pub.Fingerprint(),
        };
        signature.Signature = Convert.ToBase64String(key.Sign(Bytes(signature)));
        return signature;
    }

    public static SignatureCheck Check(Recipe recipe, RecipeSignature? signature, IReadOnlyList<TrustedConsole> trusted)
    {
        if (signature is null)
        {
            return new SignatureCheck(SignatureState.Unsigned, "recept nie je podpísaný");
        }

        if (!string.Equals(signature.Fingerprint, RecipeText.Fingerprint(recipe), StringComparison.OrdinalIgnoreCase)
            || signature.Recipe != recipe.Name || signature.Version != recipe.Version)
        {
            return new SignatureCheck(SignatureState.ContentChanged, "recept sa po podpísaní zmenil – podpis neplatí");
        }

        var console = trusted.FirstOrDefault(t => string.Equals(t.Fingerprint, signature.ConsoleKey, StringComparison.OrdinalIgnoreCase));
        if (console is null)
        {
            return new SignatureCheck(SignatureState.UnknownKey, $"podpísala ho konzola {signature.Console} ({signature.ConsoleKey}), ktorej toto PC nedôveruje");
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(signature.Signature);
        }
        catch (FormatException)
        {
            return new SignatureCheck(SignatureState.Broken, "podpis je poškodený");
        }

        using var key = VerifyingKey.FromSubjectPublicKeyInfo(console.PublicKey);
        return key.Fingerprint() == console.Fingerprint && key.Verify(Bytes(signature), raw)
            ? new SignatureCheck(SignatureState.Valid, $"podpísala konzola {console.Name} {signature.SignedUtc.ToLocalTime():d. M. yyyy H:mm}")
            : new SignatureCheck(SignatureState.Broken, "podpis nesedí – recept alebo podpis je podvrhnutý alebo poškodený");
    }

    /// <summary>A new key of a console: the private file (encrypted by the password) and the public file.</summary>
    public static (ConsoleKeyFile Private, ConsoleKeyFile Public) NewConsoleKey(string name, string password)
    {
        using var key = SigningKey.Create();
        using var pub = key.Public();
        var common = new ConsoleKeyFile { Name = name, Fingerprint = pub.Fingerprint(), PublicKey = Convert.ToBase64String(pub.Export()) };
        var secret = new ConsoleKeyFile
        {
            Name = name,
            Fingerprint = common.Fingerprint,
            PublicKey = common.PublicKey,
            EncryptedPrivateKey = Convert.ToBase64String(key.ExportEncrypted(password)),
        };
        return (secret, common);
    }

    public static SigningKey OpenPrivate(ConsoleKeyFile file, string password)
    {
        if (file.EncryptedPrivateKey is not { Length: > 0 } encrypted)
        {
            throw new InvalidDataException("Toto je verejný kľúč – na podpis treba súkromný kľúč konzoly (.kluc).");
        }

        try
        {
            return SigningKey.FromEncrypted(Convert.FromBase64String(encrypted), password);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("Nesprávne heslo ku kľúču konzoly, alebo je súbor kľúča poškodený.");
        }
    }

    public static TrustedConsole Trust(ConsoleKeyFile file)
    {
        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(file.PublicKey);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("Verejný kľúč v súbore je poškodený.");
        }

        using var key = VerifyingKey.FromSubjectPublicKeyInfo(spki);
        if (key.Fingerprint() != file.Fingerprint)
        {
            throw new InvalidDataException("Odtlačok v súbore nesedí s verejným kľúčom – súbor nepoužijem.");
        }

        return new TrustedConsole(file.Name, file.Fingerprint, spki);
    }

    public static string ToJson(RecipeSignature s) => JsonText.Readable(new JsonObject()
        .Add("schema", s.Schema)
        .Add("recipe", s.Recipe)
        .Add("version", s.Version)
        .Add("fingerprint", s.Fingerprint)
        .Add("signedUtc", s.SignedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        .Add("console", s.Console)
        .Add("consoleKey", s.ConsoleKey)
        .Add("signature", s.Signature));

    public static string ToJson(ConsoleKeyFile k) => JsonText.Readable(new JsonObject()
        .Add("schema", k.Schema)
        .Add("name", k.Name)
        .Add("fingerprint", k.Fingerprint)
        .Add("publicKey", k.PublicKey)
        .AddIf("encryptedPrivateKey", k.EncryptedPrivateKey is { } secret ? (JsonNode)secret : null));

    public static RecipeSignature SignatureFromJson(string json) => Read(json, "podpis", f => new RecipeSignature
    {
        Schema = f.Int("schema", 1),
        Recipe = f.String("recipe"),
        Version = f.Int("version", 0),
        Fingerprint = f.String("fingerprint"),
        SignedUtc = f.Time("signedUtc"),
        Console = f.String("console"),
        ConsoleKey = f.String("consoleKey"),
        Signature = f.String("signature"),
    });

    public static ConsoleKeyFile KeyFromJson(string json) => Read(json, "kľúč konzoly", f => new ConsoleKeyFile
    {
        Schema = f.Int("schema", 1),
        Name = f.String("name"),
        Fingerprint = f.String("fingerprint"),
        PublicKey = f.String("publicKey"),
        EncryptedPrivateKey = f.OptionalString("encryptedPrivateKey"),
    });

    private static T Read<T>(string json, string what, Func<JsonFields, T> map)
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json), what);
            var value = map(f);
            f.End();
            return value;
        }
        catch (JsonError e)
        {
            throw new InvalidDataException($"Súbor ({what}) sa nedá prečítať: {e.Message}", e);
        }
    }

    private static byte[] Bytes(RecipeSignature s) => Framing.Build(
        Tag,
        s.Fingerprint.ToLowerInvariant(),
        s.Recipe,
        s.Version.ToString(CultureInfo.InvariantCulture),
        s.SignedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        s.Console,
        s.ConsoleKey);
}
