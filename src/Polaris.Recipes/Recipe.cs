using System.Security.Cryptography;
using Polaris.Json;

namespace Polaris.Recipes;

/// <summary>A recipe: what a PC of a profile should have. It is text, has a version and a fingerprint, so every PC can
/// prove it has exactly the approved version.</summary>
public sealed class Recipe
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Name of the profile (e.g. KANCELARSKY).</summary>
    public string Name { get; set; } = "";

    /// <summary>Number of the version of the profile this recipe describes.</summary>
    public int Version { get; set; }

    public string Note { get; set; } = "";

    /// <summary>The elements, in the order they are applied.</summary>
    public List<Element> Elements { get; set; } = [];
}

/// <summary>Reading, writing and the fingerprint of recipes.</summary>
public static class RecipeText
{
    /// <summary>The recipe in the readable form (for people and files).</summary>
    public static string ToJson(Recipe recipe) => JsonText.Readable(RecipeFormat.Write(recipe));

    /// <summary>Reads a recipe; a broken text, an unknown field or a recipe from a newer program gives an
    /// <see cref="InvalidDataException"/> with a Slovak message saying where.</summary>
    public static Recipe FromJson(string json) => Read(() => JsonReader.Parse(json, JsonSyntax.Human));

    public static Recipe Load(string path) => Read(() => JsonReader.ParseFile(path, JsonSyntax.Human));

    /// <summary>SHA-256 of the recipe in its canonical form (docs/kanonicky-tvar.md): no spaces, no comments, fields with
    /// default values left out, texts in UTF-8. Two recipes with the same content always have the same fingerprint,
    /// however they were written – and it never changes with a new version of .NET.</summary>
    public static string Fingerprint(Recipe recipe) => JsonText.Fingerprint(RecipeFormat.Write(recipe));

    /// <summary>The canonical text whose SHA-256 is the fingerprint.</summary>
    public static string Canonical(Recipe recipe) => JsonText.Canonical(RecipeFormat.Write(recipe));

    /// <summary>SHA-256 of a file (an installer in the store), small hex letters.</summary>
    public static string FileFingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static Recipe Read(Func<JsonNode> parse)
    {
        try
        {
            return RecipeFormat.Read(parse());
        }
        catch (JsonError e)
        {
            throw new InvalidDataException("Recept sa nedá prečítať: " + e.Message, e);
        }
    }
}
