using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Polaris.Json;
using Polaris.Recipes;

namespace Polaris.Net;

/// <summary>A recipe as it travels in a publication: its fingerprint, its text and the text of its signature.</summary>
public sealed record PublishedRecipe(string Fingerprint, string Recipe, string Signature);

/// <summary>What a console hands to the centre: signed plans of groups and the signed recipes they name. Every piece is
/// signed on its own, so the centre (and later every PC) checks each of them – the upload's own signature only says
/// which console sent it, and that the bytes were meant for this centre now.</summary>
public sealed record PublicationBundle(List<SignedPlan> Plans, List<PublishedRecipe> Recipes)
{
    public string ToJson() => JsonText.Canonical(new JsonObject()
        .Add("plans", JsonArray.Of(Plans, p => JsonReader.Parse(PlanSigning.ToJson(p))))
        .Add("recipes", JsonArray.Of(Recipes, r => new JsonObject().Add("fingerprint", r.Fingerprint).Add("recipe", r.Recipe).Add("signature", r.Signature))));

    public static PublicationBundle FromJson(string json)
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json, JsonSyntax.Strict with { MaxLength = 16 * 1024 * 1024 }), "zverejnenie");
            var plans = f.Array("plans").Select(n => PlanSigning.FromJson(JsonText.Canonical(n))).ToList();
            var recipes = f.Array("recipes").Select(n =>
            {
                var r = new JsonFields(n, "recept zverejnenia");
                var published = new PublishedRecipe(r.String("fingerprint"), r.String("recipe"), r.String("signature"));
                r.End();
                return published;
            }).ToList();
            f.End();
            return new PublicationBundle(plans, recipes);
        }
        catch (JsonError e)
        {
            throw new InvalidDataException("Zverejnenie sa nedá prečítať: " + e.Message, e);
        }
    }
}

/// <summary>The upload's envelope: which console, when, for which centre, over which bytes.</summary>
public static partial class UploadSigning
{
    public const string ConsoleHeader = "X-Polaris-Konzola";
    public const string TimeHeader = "X-Polaris-Cas";
    public const string SignatureHeader = "X-Polaris-Podpis";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static ReadOnlySpan<byte> Tag => "POLUPL01"u8;

    public static Dictionary<string, string> Headers(byte[] body, string path, string serverPin, SigningKey key, DateTimeOffset now)
    {
        using var pub = key.Public();
        var time = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return new Dictionary<string, string>
        {
            [ConsoleHeader] = pub.Fingerprint(),
            [TimeHeader] = time,
            [SignatureHeader] = Convert.ToBase64String(key.Sign(Bytes(body, path, serverPin, time))),
        };
    }

    /// <summary>The console that sent it, or the reason it is refused.</summary>
    public static (TrustedConsole? Console, string Problem) Verify(Func<string, string?> header, byte[] body, string path, string serverPin, IReadOnlyList<TrustedConsole> trusted, DateTimeOffset now)
    {
        if (header(ConsoleHeader) is not { } fingerprint || header(TimeHeader) is not { } time || header(SignatureHeader) is not { } signature)
        {
            return (null, "odovzdanie nemá podpis konzoly");
        }

        var console = trusted.FirstOrDefault(t => string.Equals(t.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        if (console is null)
        {
            return (null, "konzole, ktorá odovzdáva, centrum nedôveruje (polaris-centrum server doverovat KONZOLA.verejny.json)");
        }

        if (!DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) || (now - at).Duration() > Window)
        {
            return (null, "čas odovzdania sa líši od času centra o viac ako 10 minút – skontroluj hodiny konzoly a servera");
        }

        try
        {
            using var key = VerifyingKey.FromSubjectPublicKeyInfo(console.PublicKey);
            return key.Verify(Bytes(body, path, serverPin, time), Convert.FromBase64String(signature))
                ? (console, "")
                : (null, "podpis odovzdania nesedí");
        }
        catch (FormatException)
        {
            return (null, "podpis odovzdania je poškodený");
        }
    }

    private static byte[] Bytes(byte[] body, string path, string serverPin, string time) =>
        Framing.Build(Tag, Convert.ToHexStringLower(SHA256.HashData(body)), path, serverPin.ToLowerInvariant(), time);
}

/// <summary>The centre's folder of publications: what PCs read. Writing it checks every plan and recipe against the
/// consoles the centre trusts, takes only plans newer than the published ones, writes recipes before plans and every
/// file whole (beside, then moved over).</summary>
public sealed partial class PublicationFolder(string root)
{
    private static readonly Lock Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false);

    public string Root { get; } = root;

    public string PlanFile(string group) => Path.Combine(Root, "skupiny", group + ".json");

    public string RecipeFile(string fingerprint) => Path.Combine(Root, "recepty", fingerprint + ".json");

    /// <summary>Takes a bundle; lines for people and whether anything was refused.</summary>
    public (List<string> Lines, bool Refused) Accept(PublicationBundle bundle, IReadOnlyList<TrustedConsole> trusted)
    {
        var lines = new List<string>();
        var refused = false;
        lock (Gate)
        {
            Directory.CreateDirectory(Path.Combine(Root, "recepty"));
            Directory.CreateDirectory(Path.Combine(Root, "skupiny"));
            foreach (var r in bundle.Recipes)
            {
                if (!Sha256().IsMatch(r.Fingerprint))
                {
                    lines.Add($"recept {r.Fingerprint}: neplatný odtlačok – odmietnutý");
                    refused = true;
                    continue;
                }

                try
                {
                    var recipe = RecipeText.FromJson(r.Recipe);
                    var check = RecipeSigning.Check(recipe, RecipeSigning.SignatureFromJson(r.Signature), trusted);
                    if (RecipeText.Fingerprint(recipe) != r.Fingerprint || check.State != SignatureState.Valid)
                    {
                        lines.Add($"recept {recipe.Name}: {(check.State == SignatureState.Valid ? "odtlačok nesedí" : check.Message)} – odmietnutý");
                        refused = true;
                        continue;
                    }

                    Write(RecipeFile(r.Fingerprint), r.Recipe);
                    Write(RecipeFile(r.Fingerprint)[..^5] + ".podpis", r.Signature);
                }
                catch (InvalidDataException e)
                {
                    lines.Add($"recept {r.Fingerprint[..12]}…: {e.Message} – odmietnutý");
                    refused = true;
                }
            }

            foreach (var signed in bundle.Plans)
            {
                var plan = signed.Plan;
                if (!GroupName().IsMatch(plan.Group))
                {
                    lines.Add($"plán „{plan.Group}“: neplatné meno skupiny – odmietnutý");
                    refused = true;
                    continue;
                }

                if (PlanSigning.Check(signed, trusted) is { State: not SignatureState.Valid } bad)
                {
                    lines.Add($"plán {plan.Group}: {bad.Message} – odmietnutý");
                    refused = true;
                    continue;
                }

                if (plan.Profiles.FirstOrDefault(p => !File.Exists(RecipeFile(p.Fingerprint))) is { } missing)
                {
                    lines.Add($"plán {plan.Group}: chýba recept profilu {missing.Name} – odmietnutý");
                    refused = true;
                    continue;
                }

                if (Published(plan.Group) is { } current && current >= plan.Sequence)
                {
                    lines.Add($"plán {plan.Group}: centrum už má rovnaký alebo novší ({current}) – ponechaný");
                    continue;
                }

                Write(PlanFile(plan.Group), PlanSigning.ToJson(signed));
                lines.Add($"plán {plan.Group}: zverejnený ({plan.Sequence})");
            }
        }

        return (lines, refused);
    }

    /// <summary>The sequence of the published plan of a group, or null.</summary>
    public long? Published(string group)
    {
        try
        {
            return File.Exists(PlanFile(group)) ? PlanSigning.FromJson(File.ReadAllText(PlanFile(group), Utf8)).Plan.Sequence : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A bundle of what is published for these groups (the console's side, before uploading).</summary>
    public PublicationBundle Bundle(IEnumerable<string> groups)
    {
        var plans = groups.Select(g => PlanSigning.FromJson(File.ReadAllText(PlanFile(g), Utf8))).ToList();
        var recipes = plans.SelectMany(p => p.Plan.Profiles).Select(p => p.Fingerprint).Distinct()
            .Select(f => new PublishedRecipe(f, File.ReadAllText(RecipeFile(f), Utf8), File.ReadAllText(RecipeFile(f)[..^5] + ".podpis", Utf8)))
            .ToList();
        return new PublicationBundle(plans, recipes);
    }

    private static void Write(string path, string text)
    {
        var temp = path + ".novy";
        File.WriteAllText(temp, text, Utf8);
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex GroupName();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();
}
