using System.Globalization;
using System.Text.RegularExpressions;

namespace Polaris.Recipes;

/// <summary>Languages of input Windows knows by their usual layout. Another layout or another language is written in the
/// recipe as "LANGID:LAYOUT" (e.g. "041B:0001041B").</summary>
public static partial class KeyboardLayouts
{
    public static readonly IReadOnlyDictionary<string, (string LangId, string Layout)> Known =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["sk-SK"] = ("041B", "0000041B"),
            ["cs-CZ"] = ("0405", "00000405"),
            ["en-US"] = ("0409", "00000409"),
            ["en-GB"] = ("0809", "00000809"),
            ["de-DE"] = ("0407", "00000407"),
            ["de-AT"] = ("0C07", "00000407"),
            ["fr-FR"] = ("040C", "0000040C"),
            ["ru-RU"] = ("0419", "00000419"),
            ["hu-HU"] = ("040E", "0000040E"),
            ["pl-PL"] = ("0415", "00000415"),
            ["es-ES"] = ("0C0A", "0000040A"),
            ["it-IT"] = ("0410", "00000410"),
        };

    public const int MaxLanguages = 9;
    public const string PreloadKey = @"Keyboard Layout\Preload";
    public const string SubstitutesKey = @"Keyboard Layout\Substitutes";
    public const string UserProfileKey = @"Control Panel\International\User Profile";

    /// <summary>The language id and layout of a language, or null when it is neither known nor given in the recipe.</summary>
    public static (string LangId, string Layout)? Of(string tag, IReadOnlyDictionary<string, string>? layouts)
    {
        if (layouts is not null && layouts.TryGetValue(tag, out var given))
        {
            if (FullPattern().Match(given) is { Success: true } full)
            {
                return (full.Groups[1].Value.ToUpperInvariant(), full.Groups[2].Value.ToUpperInvariant());
            }

            return LayoutPattern().IsMatch(given) && Known.TryGetValue(tag, out var k) ? (k.LangId, given.ToUpperInvariant()) : null;
        }

        return Known.TryGetValue(tag, out var known) ? known : null;
    }

    /// <summary>The values in the template of profiles: the list of loaded layouts (with substitutes for unusual ones) and
    /// the list of languages Windows shows. Places of languages left over from a longer older list are removed.</summary>
    public static IEnumerable<Element> Expand(KeyboardsElement k)
    {
        var index = 0;
        foreach (var tag in k.Languages)
        {
            index++;
            if (Of(tag, k.Layouts) is not { } pair)
            {
                continue; // the check reports it
            }

            var preload = ("0000" + pair.LangId).ToLowerInvariant();
            yield return Value(k, $"{k.Id}.preload{index}", PreloadKey, index.ToString(CultureInfo.InvariantCulture), RegistryKind.String, preload);
            yield return Value(k, $"{k.Id}.nahrada{index}", SubstitutesKey, preload, RegistryKind.String, pair.Layout.ToLowerInvariant(),
                present: k.Present && !pair.Layout.Equals("0000" + pair.LangId, StringComparison.OrdinalIgnoreCase));
            yield return Value(k, $"{k.Id}.jazyk{index}", UserProfileKey + "\\" + tag, $"{pair.LangId}:{pair.Layout}", RegistryKind.DWord, "1");
        }

        yield return Value(k, $"{k.Id}.jazyky", UserProfileKey, "Languages", RegistryKind.MultiString, string.Join('\n', k.Languages));
        for (var rest = index + 1; rest <= MaxLanguages; rest++)
        {
            yield return Value(k, $"{k.Id}.preload{rest}", PreloadKey, rest.ToString(CultureInfo.InvariantCulture), RegistryKind.String, "", present: false);
        }
    }

    private static UserRegistryElement Value(KeyboardsElement k, string id, string path, string name, RegistryKind kind, string data, bool? present = null) => new()
    {
        Id = id,
        Present = present ?? k.Present,
        Note = k.Title,
        Path = path,
        Name = name,
        Kind = kind,
        Data = data,
    };

    [GeneratedRegex("^([0-9A-Fa-f]{4}):([0-9A-Fa-f]{8})$")]
    public static partial Regex FullPattern();

    [GeneratedRegex("^[0-9A-Fa-f]{8}$")]
    public static partial Regex LayoutPattern();
}

/// <summary>When the cleaner of sessions deletes the profile of a shared account.</summary>
public static class SessionPolicy
{
    /// <summary>Accounts that are never cleaned, whatever a recipe says.</summary>
    public static readonly IReadOnlySet<string> NeverClean = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Administrator", "Administrátor", "Guest", "Hosť", "DefaultAccount", "WDAGUtilityAccount", "SYSTEM", "defaultuser0",
    };

    /// <param name="loaded">The account is signed in (its profile is loaded) – never cleaned then.</param>
    /// <param name="lastUsed">When the profile was last loaded (null = unknown, treated as long ago).</param>
    public static bool ShouldClean(SessionCleaning mode, bool loaded, bool profileExists, DateTime? lastUsed, DateTime now)
    {
        if (loaded || !profileExists)
        {
            return false;
        }

        return mode switch
        {
            SessionCleaning.AtSignOut => true,
            SessionCleaning.EndOfDay => lastUsed is not { } d || d.Date < now.Date,
            SessionCleaning.EndOfWeek => lastUsed is not { } w || WeekStart(w) < WeekStart(now),
            _ => false,
        };
    }

    private static DateTime WeekStart(DateTime t) => t.Date.AddDays(1 - Polaris.Core.Timetable.IsoWeekday(DateOnly.FromDateTime(t)));
}
