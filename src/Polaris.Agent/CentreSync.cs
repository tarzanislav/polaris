using System.Text;
using Polaris.Net;
using Polaris.Recipes;
using Polaris.Web;

namespace Polaris.Agent;

/// <summary>Where the centre is and which group this PC belongs to. The pin is the SHA-256 of the centre's public key.</summary>
public sealed record CentreSettings(Uri Address, string Pin, string Group)
{
    public Uri Of(string path) => new(Address, path);
}

/// <summary>Takes the plan of the PC's group from the centre and the recipes it names, and keeps them in the agent's own
/// folder. Nothing is kept that a trusted console did not sign: the plan, every recipe by its fingerprint, and never a
/// plan older than the one the PC already has (an old plan cannot be sent again to turn the clock back).</summary>
public sealed class CentreSync(CentreSettings settings, string cache, Func<IReadOnlyList<TrustedConsole>> trusted)
{
    public const long MaxPlan = 1024 * 1024;
    public const long MaxRecipe = 4 * 1024 * 1024;
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8 = new(false);

    private string? _etag;

    public sealed record Outcome(bool Changed, long Sequence, string Message);

    public string PlanFile => Path.Combine(cache, "plan.json");

    public string RecipeFolder => Path.Combine(cache, "recepty");

    /// <summary>One look at the centre. <paramref name="known"/> is the sequence of the plan the PC has.</summary>
    public Outcome Fetch(long known)
    {
        var consoles = trusted();
        if (consoles.Count == 0)
        {
            return new Outcome(false, known, "PC nedôveruje žiadnej konzole – plán z centra neprijme (polaris-agent doverovat KLUC).");
        }

        var answer = PinnedClient.Get(settings.Of($"v1/skupiny/{settings.Group}.json"), settings.Pin, _etag, MaxPlan, Limit);
        if (answer.NotModified)
        {
            return new Outcome(false, known, "plán sa nezmenil");
        }

        if (!answer.Ok)
        {
            return new Outcome(false, known, answer.Error ?? (answer.Status == 404 ? $"centrum nemá plán skupiny {settings.Group}" : $"centrum odpovedalo {answer.Status}"));
        }

        SignedPlan signed;
        try
        {
            signed = PlanSigning.FromJson(Utf8.GetString(answer.Body));
        }
        catch (InvalidDataException e)
        {
            return new Outcome(false, known, e.Message);
        }

        var plan = signed.Plan;
        if (plan.Group != settings.Group)
        {
            return new Outcome(false, known, $"centrum poslalo plán skupiny {plan.Group}, nie {settings.Group} – odmietnutý");
        }

        if (PlanSigning.Check(signed, consoles) is { State: not SignatureState.Valid } bad)
        {
            return new Outcome(false, known, bad.Message + " – odmietnutý");
        }

        if (plan.Sequence < known)
        {
            return new Outcome(false, known, $"centrum ponúka starší plán ({plan.Sequence}) ako má PC ({known}) – odmietnutý");
        }

        if (plan.Sequence == known && File.Exists(PlanFile))
        {
            _etag = answer.ETag;
            return new Outcome(false, known, "plán sa nezmenil");
        }

        Directory.CreateDirectory(RecipeFolder);
        foreach (var profile in plan.Profiles)
        {
            if (Recipe(profile.Fingerprint, consoles) is not null)
            {
                continue; // already here and still valid
            }

            var recipe = PinnedClient.Get(settings.Of($"v1/recepty/{profile.Fingerprint}.json"), settings.Pin, null, MaxRecipe, Limit);
            var signature = PinnedClient.Get(settings.Of($"v1/recepty/{profile.Fingerprint}.podpis"), settings.Pin, null, MaxPlan, Limit);
            if (!recipe.Ok || !signature.Ok)
            {
                return new Outcome(false, known, $"recept profilu {profile.Name} sa nedá stiahnuť ({recipe.Error ?? signature.Error ?? recipe.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
            }

            var file = Path.Combine(RecipeFolder, profile.Fingerprint + ".json");
            WriteSafely(file + ".podpis", signature.Body);
            WriteSafely(file, recipe.Body);
            if (Recipe(profile.Fingerprint, consoles) is null)
            {
                File.Delete(file);
                File.Delete(file + ".podpis");
                return new Outcome(false, known, $"recept profilu {profile.Name} nesedí s plánom alebo nemá platný podpis – odmietnutý");
            }
        }

        WriteSafely(PlanFile, answer.Body);
        _etag = answer.ETag;
        return new Outcome(true, plan.Sequence, $"nový plán {plan.Sequence} (profily {string.Join(", ", plan.Profiles.Select(p => p.Name))})");
    }

    /// <summary>The plan kept by the PC, checked again; null when there is none or it no longer passes.</summary>
    public GroupPlan? Plan()
    {
        if (!File.Exists(PlanFile))
        {
            return null;
        }

        try
        {
            var signed = PlanSigning.FromJson(File.ReadAllText(PlanFile, Utf8));
            return signed.Plan.Group == settings.Group && PlanSigning.Check(signed, trusted()).State == SignatureState.Valid ? signed.Plan : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A kept recipe by its fingerprint, when its content and its signature still fit; else null.</summary>
    public Recipe? Recipe(string fingerprint, IReadOnlyList<TrustedConsole>? consoles = null)
    {
        var file = Path.Combine(RecipeFolder, fingerprint + ".json");
        if (!File.Exists(file) || !File.Exists(file + ".podpis"))
        {
            return null;
        }

        try
        {
            var recipe = RecipeText.Load(file);
            var signature = RecipeSigning.SignatureFromJson(File.ReadAllText(file + ".podpis", Utf8));
            return RecipeText.Fingerprint(recipe) == fingerprint
                && RecipeSigning.Check(recipe, signature, consoles ?? trusted()).State == SignatureState.Valid
                ? recipe
                : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static void WriteSafely(string path, byte[] bytes)
    {
        var temp = path + ".novy";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Which profile should run now and whether to apply it: when the profile or its recipe changed, or once an
/// hour anyway (a recipe keeps the PC as it should be – what drifted is put back).</summary>
public static class CentrePolicy
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(1);

    public static (string Profile, string Fingerprint)? Wanted(GroupPlan plan, DateTime local) =>
        plan.Find(plan.ProfileAt(local)) is { } p ? (p.Name, p.Fingerprint) : null;

    public static bool ShouldApply((string Profile, string Fingerprint) wanted, string? lastProfile, string? lastFingerprint, DateTimeOffset? lastApplied, DateTimeOffset now) =>
        wanted.Profile != lastProfile || wanted.Fingerprint != lastFingerprint || lastApplied is null || now - lastApplied >= Keep;
}
