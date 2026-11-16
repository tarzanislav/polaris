using System.Globalization;
using Polaris.Json;
using Polaris.Recipes;

namespace Polaris.Net;

/// <summary>A plan as it is published: the plan and the signature of a console over its fingerprint, in one file.</summary>
public sealed record SignedPlan(GroupPlan Plan, RecipeSignature Signature);

/// <summary>Signing and checking plans of groups. The signed bytes carry their own tag, so a signature of a plan can never
/// pass as a signature of a recipe and the other way round.</summary>
public static class PlanSigning
{
    private static ReadOnlySpan<byte> Tag => "POLPLN01"u8;

    public static SignedPlan Sign(GroupPlan plan, SigningKey key, string console)
    {
        using var pub = key.Public();
        var signature = new RecipeSignature
        {
            Recipe = "plan:" + plan.Group,
            Version = 0,
            Fingerprint = GroupPlanFormat.Fingerprint(plan),
            SignedUtc = plan.IssuedUtc,
            Console = console,
            ConsoleKey = pub.Fingerprint(),
        };
        signature.Signature = Convert.ToBase64String(key.Sign(Bytes(plan, signature)));
        return new SignedPlan(plan, signature);
    }

    public static SignatureCheck Check(SignedPlan signed, IReadOnlyList<TrustedConsole> trusted)
    {
        var (plan, signature) = signed;
        if (signature.Recipe != "plan:" + plan.Group || !string.Equals(signature.Fingerprint, GroupPlanFormat.Fingerprint(plan), StringComparison.OrdinalIgnoreCase))
        {
            return new SignatureCheck(SignatureState.ContentChanged, "plán sa po podpísaní zmenil – podpis neplatí");
        }

        var console = trusted.FirstOrDefault(t => string.Equals(t.Fingerprint, signature.ConsoleKey, StringComparison.OrdinalIgnoreCase));
        if (console is null)
        {
            return new SignatureCheck(SignatureState.UnknownKey, $"plán podpísala konzola {signature.Console}, ktorej toto PC nedôveruje");
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(signature.Signature);
        }
        catch (FormatException)
        {
            return new SignatureCheck(SignatureState.Broken, "podpis plánu je poškodený");
        }

        using var key = VerifyingKey.FromSubjectPublicKeyInfo(console.PublicKey);
        return key.Fingerprint() == console.Fingerprint && key.Verify(Bytes(plan, signature), raw)
            ? new SignatureCheck(SignatureState.Valid, $"plán podpísala konzola {console.Name}")
            : new SignatureCheck(SignatureState.Broken, "podpis plánu nesedí – plán je podvrhnutý alebo poškodený");
    }

    public static string ToJson(SignedPlan s) => JsonText.Readable(new JsonObject()
        .Add("plan", GroupPlanFormat.Write(s.Plan))
        .Add("signature", JsonReader.Parse(RecipeSigning.ToJson(s.Signature))));

    public static SignedPlan FromJson(string json)
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json), "zverejnený plán");
            var plan = GroupPlanFormat.Read(f.Node("plan") ?? throw new InvalidDataException("Zverejnený plán nemá plán."));
            var signatureNode = f.Node("signature") ?? throw new InvalidDataException("Zverejnený plán nemá podpis.");
            f.End();
            return new SignedPlan(plan, RecipeSigning.SignatureFromJson(JsonText.Canonical(signatureNode)));
        }
        catch (JsonError e)
        {
            throw new InvalidDataException("Plán sa nedá prečítať: " + e.Message, e);
        }
    }

    private static byte[] Bytes(GroupPlan plan, RecipeSignature s) => Framing.Build(
        Tag,
        s.Fingerprint.ToLowerInvariant(),
        plan.Group,
        plan.Sequence.ToString(CultureInfo.InvariantCulture),
        s.SignedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        s.Console,
        s.ConsoleKey);
}
