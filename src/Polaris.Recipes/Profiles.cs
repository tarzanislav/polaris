namespace Polaris.Recipes;

/// <summary>A profile put together from recipes (decided 26. 11. 2026: profiles in the console): one recipe with the
/// elements of all its parts in their order. A PC runs one recipe per profile, so nothing changes for it – it gets one
/// signed recipe, as always.</summary>
public static class ProfileComposer
{
    public const int MaxParts = 20;

    /// <summary>The recipe of the profile. An element whose id another part already uses gets the part's short name in
    /// front (e.g. sluzby.telemetria), so every id stays unique.</summary>
    public static Recipe Compose(string profile, IReadOnlyList<(string Id, Recipe Recipe)> parts, int version)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var elements = new List<Element>();
        foreach (var (id, recipe) in parts)
        {
            var prefix = Short(id);
            foreach (var element in recipe.Elements)
            {
                if (!ids.Add(element.Id))
                {
                    var renamed = (prefix + "." + element.Id) is var full && full.Length > 64 ? full[..64] : prefix + "." + element.Id;
                    element.Id = renamed;
                    ids.Add(renamed);
                }

                elements.Add(element);
            }
        }

        return new Recipe
        {
            Name = profile,
            Version = version,
            Note = "Profil " + profile + ": " + string.Join(" + ", parts.Select(p => $"{p.Recipe.Name} v{p.Recipe.Version}")),
            Elements = elements,
        };
    }

    /// <summary>The short name of a part for ids: ukazka-sluzby → sluzby, vlastne/moje-programy → moje-programy.</summary>
    public static string Short(string id)
    {
        var name = id[(id.LastIndexOf('/') + 1)..];
        name = name.StartsWith("ukazka-", StringComparison.Ordinal) ? name[7..] : name;
        var clean = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return clean.Length == 0 ? "cast" : clean.Length > 24 ? clean[..24] : clean;
    }

    /// <summary>A profile name as PCs and the timetable use it: capitals, digits, - and _.</summary>
    public static bool Name(string name) =>
        name.Length is >= 1 and <= 40 && name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '-' or '_');
}
