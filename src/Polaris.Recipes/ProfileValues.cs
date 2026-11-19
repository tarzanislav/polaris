using Polaris.Json;

namespace Polaris.Recipes;

/// <summary>The values of profiles a recipe gave (user-registry elements and the silent sound scheme), as the agent keeps
/// them: the template of new profiles has them, but a profile that already exists – the pupil's from the trezor – was made
/// before. The agent writes them into the existing profiles of the shared accounts and again after every return of a
/// profile from the trezor, so the trezor never brings an old value back.</summary>
public static class ProfileValueFormat
{
    public static string ToJson(Element element) => element switch
    {
        UserRegistryElement u => JsonText.Canonical(new JsonObject()
            .Add("path", UserRegistryPath.Normalize(u.Path))
            .Add("name", u.Name)
            .Add("kind", u.Kind.ToString())
            .Add("data", u.Data)
            .Add("present", u.Present)),
        SoundsElement s => JsonText.Canonical(new JsonObject().Add("sounds", s.Scheme.ToString()).Add("present", s.Present)),
        _ => throw new ArgumentException("Hodnota profilu je len hodnota registra používateľa alebo zvuková schéma.", nameof(element)),
    };

    public static Element FromJson(string json, string what = "hodnota profilu")
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json), what);
            Element element = f.Has("sounds")
                ? new SoundsElement { Scheme = f.Enum("sounds", SoundScheme.None) }
                : new UserRegistryElement
                {
                    Path = f.String("path"),
                    Name = f.String("name"),
                    Kind = f.Enum("kind", RegistryKind.String),
                    Data = f.String("data"),
                };
            element.Present = f.Bool("present", true);
            f.End();
            return element;
        }
        catch (JsonError e)
        {
            throw new InvalidDataException($"{what}: {e.Message}", e);
        }
    }

    /// <summary>Is the element a value of profiles (kept for existing profiles)?</summary>
    public static bool IsProfileValue(Element element) => element is UserRegistryElement or SoundsElement;

    /// <summary>Whether what was found in a profile is as the element wants (a silent scheme absent = not to be managed).</summary>
    public static bool Matches(Element element, Observation found) => element switch
    {
        SoundsElement s when !s.Present => true,
        _ => element.Present ? found.State == Observed.Present : found.State == Observed.Absent,
    };
}
