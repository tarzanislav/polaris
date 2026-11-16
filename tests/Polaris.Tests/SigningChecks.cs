using Polaris.Agent;
using Polaris.Net;
using Polaris.Recipes;

namespace Polaris.Tests;

public static class SigningChecks
{
    private static readonly DateTimeOffset T = new(2026, 11, 16, 21, 0, 0, TimeSpan.Zero);
    private const string Password = "dlhe heslo spravcu 2026";

    [Check]
    public static void SignatureFitsOnlyTheSignedRecipeAndTrustedKey()
    {
        var recipe = RecipeText.Load(Repo.SampleRecipe);
        var (secret, pub) = RecipeSigning.NewConsoleKey("Spravca", Password);
        using var key = RecipeSigning.OpenPrivate(secret, Password);
        var signature = RecipeSigning.SignatureFromJson(RecipeSigning.ToJson(RecipeSigning.Sign(recipe, key, "Spravca", T)));
        TrustedConsole[] trusted = [RecipeSigning.Trust(RecipeSigning.KeyFromJson(RecipeSigning.ToJson(pub)))];

        Expect.Equal(SignatureState.Valid, RecipeSigning.Check(recipe, signature, trusted).State, "platný podpis");
        Expect.Equal(SignatureState.Unsigned, RecipeSigning.Check(recipe, null, trusted).State, "bez podpisu");
        Expect.Equal(SignatureState.UnknownKey, RecipeSigning.Check(recipe, signature, []).State, "nedôveryhodná konzola");

        var changed = RecipeText.Load(Repo.SampleRecipe);
        ((RegistryValueElement)changed.Elements[3]).Data = "0";
        Expect.Equal(SignatureState.ContentChanged, RecipeSigning.Check(changed, signature, trusted).State, "zmenený recept");

        var renamed = RecipeSigning.SignatureFromJson(RecipeSigning.ToJson(signature));
        renamed.Console = "Podvodník";
        Expect.Equal(SignatureState.Broken, RecipeSigning.Check(recipe, renamed, trusted).State, "zmenené meno konzoly");
        var forged = RecipeSigning.SignatureFromJson(RecipeSigning.ToJson(signature));
        forged.Signature = "nie je base64!";
        Expect.Equal(SignatureState.Broken, RecipeSigning.Check(recipe, forged, trusted).State, "poškodený podpis");

        var (otherSecret, _) = RecipeSigning.NewConsoleKey("Iny", Password);
        using var other = RecipeSigning.OpenPrivate(otherSecret, Password);
        var stolenName = RecipeSigning.Sign(recipe, other, "Spravca", T);
        stolenName.ConsoleKey = pub.Fingerprint;
        Expect.Equal(SignatureState.Broken, RecipeSigning.Check(recipe, stolenName, trusted).State, "cudzí kľúč pod menom správcu");
    }

    [Check]
    public static void KeyFilesProtectThemselves()
    {
        var (secret, pub) = RecipeSigning.NewConsoleKey("Spravca", Password);
        Expect.Throws<InvalidDataException>(() => RecipeSigning.OpenPrivate(secret, "zle heslo"), "nesprávne heslo");
        Expect.Throws<InvalidDataException>(() => RecipeSigning.OpenPrivate(pub, Password), "verejným kľúčom sa nepodpisuje");
        Expect.True(!RecipeSigning.ToJson(pub).Contains("encryptedPrivateKey", StringComparison.Ordinal), "verejný súbor nemá súkromný kľúč");
        var bad = RecipeSigning.KeyFromJson(RecipeSigning.ToJson(pub));
        bad.Fingerprint = "0000000000000000";
        Expect.Throws<InvalidDataException>(() => RecipeSigning.Trust(bad), "nesediaci odtlačok");
    }

    [Check]
    public static void ConsoleSignsAndThePcAcceptsOnlySignedRecipes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-podpis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var recipeFile = Path.Combine(dir, "recept.json");
            File.Copy(Repo.SampleRecipe, recipeFile);
            var keyBase = Path.Combine(dir, "spravca");

            int Centre(params string[] args) =>
                Polaris.Centrum.Commands.Run(args, new StringWriter(), T.LocalDateTime, T, _ => Password);

            Expect.Equal(0, Centre("kluc", "novy", keyBase), "nový kľúč");
            Expect.Equal(2, Centre("kluc", "novy", keyBase), "kľúč sa neprepíše");
            Expect.Equal(2, Polaris.Centrum.Commands.Run(["kluc", "novy", Path.Combine(dir, "kratke")], new StringWriter(), T.LocalDateTime, T, _ => "kratke"), "krátke heslo");

            var trusted = new List<TrustedConsole>();
            var pc = new FakePc();
            var host = new AgentHost
            {
                Handlers = _ => [pc],
                IsAdmin = () => true,
                JournalPath = Path.Combine(dir, "dennik.jsonl"),
                Clock = () => T,
                Confirm = _ => true,
                Trusted = () => trusted,
                AddTrusted = k => trusted.Add(RecipeSigning.Trust(k)),
                RemoveTrusted = f => trusted.RemoveAll(t => t.Fingerprint == f) > 0,
            };

            (int Code, string Text) Agent(params string[] args)
            {
                var w = new StringWriter();
                var code = Polaris.Agent.Commands.Run(args, w, host);
                return (code, w.ToString());
            }

            Expect.Equal(2, Agent("doverovat", keyBase + ".kluc").Code, "súkromný kľúč na PC nepatrí");
            Expect.Equal(0, Agent("doverovat", keyBase + ".verejny.json").Code, "dôvera ku konzole");
            Expect.True(Agent("konzoly").Text.Contains("spravca", StringComparison.Ordinal), "zoznam konzol");

            var unsigned = Agent("pouzi", recipeFile, "--ano");
            Expect.True(unsigned.Code == 2 && unsigned.Text.Contains("nie je podpísaný", StringComparison.Ordinal), "nepodpísaný sa nepoužije: " + unsigned.Text);
            Expect.True(pc.Log.Count == 0, "nič sa nezmenilo");

            Expect.Equal(0, Centre("podpisat", recipeFile, keyBase + ".kluc"), "podpis receptu");
            var signedRun = Agent("pouzi", recipeFile, "--ano");
            Expect.True(signedRun.Code == 0 && signedRun.Text.Contains("podpísala konzola spravca", StringComparison.Ordinal), "podpísaný sa použije: " + signedRun.Text);
            Expect.True(Agent("overit", recipeFile).Text.Contains("Podpis: konzola spravca", StringComparison.Ordinal), "overit ukáže podpis");

            File.WriteAllText(recipeFile, File.ReadAllText(recipeFile).Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal));
            var changed = Agent("pouzi", recipeFile, "--ano");
            Expect.True(changed.Code == 2 && changed.Text.Contains("zmenil", StringComparison.Ordinal), "zmenený recept sa nepoužije: " + changed.Text);
            Expect.True(Agent("overit", recipeFile).Text.Contains("NEPLATÍ", StringComparison.Ordinal), "overit ukáže neplatný podpis");

            var fingerprint = trusted[0].Fingerprint;
            Expect.Equal(0, Agent("nedoverovat", fingerprint).Code, "zrušenie dôvery");
            Expect.Equal(2, Agent("nedoverovat", fingerprint).Code, "už nedôveruje");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
