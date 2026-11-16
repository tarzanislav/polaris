using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Recipe))]
internal sealed partial class RecipeJson : JsonSerializerContext;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Recipe))]
internal sealed partial class CanonicalRecipeJson : JsonSerializerContext;

/// <summary>Reading, writing and the fingerprint of recipes.</summary>
public static class RecipeText
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string ToJson(Recipe recipe) => JsonSerializer.Serialize(recipe, RecipeJson.Default.Recipe);

    /// <summary>Reads a recipe; a broken text or a recipe from a newer program gives an <see cref="InvalidDataException"/>
    /// with a Slovak message saying where.</summary>
    public static Recipe FromJson(string json)
    {
        Recipe? recipe;
        try
        {
            recipe = JsonSerializer.Deserialize(json, RecipeJson.Default.Recipe);
        }
        catch (JsonException e)
        {
            var where = e.LineNumber is { } line ? $" (riadok {line + 1}, znak {e.BytePositionInLine + 1})" : "";
            throw new InvalidDataException($"Recept sa nedá prečítať{where}: {Describe(e)}", e);
        }

        if (recipe is null)
        {
            throw new InvalidDataException("Recept je prázdny.");
        }

        if (recipe.Schema > Recipe.CurrentSchema)
        {
            throw new InvalidDataException(
                $"Recept je z novšieho programu (schéma {recipe.Schema}, tento program pozná {Recipe.CurrentSchema}).");
        }

        if (recipe.Schema < 1)
        {
            throw new InvalidDataException("Recept nemá platné číslo schémy.");
        }

        return recipe;
    }

    public static Recipe Load(string path) => FromJson(File.ReadAllText(path, Utf8));

    /// <summary>SHA-256 of the recipe in one fixed written form (no spaces, no comments), small hex letters. Two recipes
    /// with the same content always have the same fingerprint, however they were formatted.</summary>
    public static string Fingerprint(Recipe recipe)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(recipe, CanonicalRecipeJson.Default.Recipe);
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    /// <summary>SHA-256 of a file (an installer in the store), small hex letters.</summary>
    public static string FileFingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Describe(JsonException e)
    {
        var message = e.Message;
        if (message.Contains("discriminator", StringComparison.OrdinalIgnoreCase) || message.Contains("'type'", StringComparison.Ordinal))
        {
            return "prvok má neznámy alebo chýbajúci typ (\"type\": program, registry, bookmarks, service alebo feature).";
        }

        // the first sentence of the system message is enough; the position is given separately
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        return cut > 0 ? message[..cut] : message;
    }
}
