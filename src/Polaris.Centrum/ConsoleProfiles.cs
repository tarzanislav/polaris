using System.Text;
using Polaris.Json;
using Polaris.Net;
using Polaris.Recipes;

namespace Polaris.Centrum;

/// <summary>A recipe of the console's library: the samples inside the program and the console's own recipes (folder
/// recepty beside its key).</summary>
public sealed record LibraryRecipe(string Id, string Title, Recipe Recipe, bool Own);

/// <summary>Profiles in the console (decided 26. 11. 2026): a profile is put together from recipes of the library, each
/// group gets its profile, and one click signs and publishes it all to the centre – the PCs take it within 5 minutes.
/// The settings live in centrum.json beside the console's key; the composed recipes in the folder profily.</summary>
public static class ConsoleProfiles
{
    /// <summary>What the pilot ran on the test PC: a first profile that changes nothing there.</summary>
    public static readonly IReadOnlyList<string> Starter = ["ukazka-zaklad", "ukazka-sluzby", "ukazka-relacia", "ukazka-pieskovisko", "ukazka-slabe-pc"];

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["ukazka-zaklad"] = "Základ učebne – komfort a výkon",
        ["ukazka-sluzby"] = "Služby Windows – vypnuté nepotrebné",
        ["ukazka-relacia"] = "Relácia žiaka – konto ziak, profil z trezoru",
        ["ukazka-pieskovisko"] = "Pieskovisko (Windows Sandbox)",
        ["ukazka-slabe-pc"] = "Slabé PC – bez priehľadnosti",
        ["ukazka-kancelarsky"] = "Kancelársky profil (ukážka)",
        ["ukazka-softver-zaklad"] = "Softvér – základ (prehliadače, 7-Zip, PDF, VLC…)",
        ["ukazka-softver-multimedia"] = "Softvér – multimédiá",
        ["ukazka-softver-kancelaria"] = "Softvér – kancelária (LibreOffice)",
        ["ukazka-softver-ucitel"] = "Softvér – učiteľ",
    };

    private static string Order(string id) => Titles.Keys.ToList().IndexOf(id) is var i and >= 0 ? i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) : "9" + id;

    /// <summary>The library: the samples inside the program, then the console's own recipes (recepty/*.json beside it).</summary>
    public static List<LibraryRecipe> Library(string folder)
    {
        var list = new List<LibraryRecipe>();
        var assembly = typeof(ConsoleProfiles).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("recepty/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var id = Path.GetFileNameWithoutExtension(name);
            var recipe = RecipeText.FromJson(reader.ReadToEnd());
            list.Add(new LibraryRecipe(id, Titles.GetValueOrDefault(id) ?? recipe.Name, recipe, false));
        }

        var own = Path.Combine(folder, "recepty");
        if (Directory.Exists(own))
        {
            foreach (var file in Directory.GetFiles(own, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    var recipe = RecipeText.Load(file);
                    list.Add(new LibraryRecipe("vlastne/" + Path.GetFileNameWithoutExtension(file), recipe.Name + " (vlastný)", recipe, true));
                }
                catch (InvalidDataException)
                {
                    // a broken own recipe is not offered; the console's check names it on publishing
                }
            }
        }

        return list.OrderBy(r => r.Own).ThenBy(r => Order(r.Id), StringComparer.Ordinal).ToList();
    }

    private static ClassroomStore Store(string folder) => new(Path.Combine(folder, "centrum.json"));

    /// <summary>The profiles, the library and the groups for the window.</summary>
    public static JsonObject State(string folder)
    {
        var (data, note) = Store(folder).Load();
        var library = Library(folder);
        return new JsonObject()
            .Add("ok", true)
            .Add("message", note ?? "")
            .Add("library", JsonArray.Of(library, r => new JsonObject()
                .Add("id", r.Id)
                .Add("title", r.Title)
                .Add("name", r.Recipe.Name)
                .Add("version", r.Recipe.Version)
                .Add("note", r.Recipe.Note)
                .Add("count", r.Recipe.Elements.Count)
                .Add("own", r.Own)))
            .Add("profiles", JsonArray.Of(data.Profiles, p => new JsonObject()
                .Add("name", p.Name)
                .Add("parts", JsonArray.Of(p.Parts, x => x))
                .Add("composed", p.Parts.Count > 0)
                .Add("description", p.Description)))
            .Add("groups", JsonArray.Of(data.Groups, g => new JsonObject().Add("name", g.Name).Add("profile", g.DefaultProfile)))
            .Add("starter", JsonArray.Of(Starter, x => x));
    }

    /// <summary>Creates or changes a profile put together from the library.</summary>
    public static JsonObject SaveProfile(string folder, byte[] body, DateTimeOffset now)
    {
        var f = new JsonFields(JsonReader.Parse(Encoding.UTF8.GetString(body), JsonSyntax.Strict), "profil");
        var name = f.String("name").Trim().ToUpperInvariant();
        var parts = f.Strings("parts").Distinct(StringComparer.Ordinal).ToList();
        f.End();
        if (!ProfileComposer.Name(name))
        {
            return Fail("Meno profilu: veľké písmená bez diakritiky, číslice, - a _ (napr. UCEBNA, PISOMKA).");
        }

        var known = Library(folder).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (parts.Count == 0 || parts.Count > ProfileComposer.MaxParts || parts.Any(p => !known.Contains(p)))
        {
            return Fail("Profil potrebuje aspoň jeden recept z knižnice.");
        }

        var store = Store(folder);
        var (data, _) = store.Load();
        var profile = data.FindProfile(name);
        if (profile is { Parts.Count: 0 })
        {
            return Fail($"Profil {name} je písaný ručne – v konzole ho nemeň (alebo zvoľ iné meno).");
        }

        if (profile is null)
        {
            profile = new ProfileRecipe { Name = name, File = $"profily/{name.ToLowerInvariant()}.json" };
            data.Profiles.Add(profile);
        }

        profile.Parts = parts;
        store.Save(data, now);
        return new JsonObject().Add("ok", true).Add("message", $"Profil {name} uložený – zverejni ho, aby ho PC dostali.");
    }

    public static JsonObject DeleteProfile(string folder, byte[] body, DateTimeOffset now)
    {
        var f = new JsonFields(JsonReader.Parse(Encoding.UTF8.GetString(body), JsonSyntax.Strict), "profil");
        var name = f.String("name");
        f.End();
        var store = Store(folder);
        var (data, _) = store.Load();
        if (data.Groups.FirstOrDefault(g => g.DefaultProfile == name) is { } used)
        {
            return Fail($"Profil {name} používa skupina {used.Name} – najprv jej daj iný.");
        }

        if (data.Timetable.Lessons.Any(l => l.Profile == name) || data.Timetable.Overrides.Any(o => o.Profile == name))
        {
            return Fail($"Profil {name} je v rozvrhu.");
        }

        data.Profiles.RemoveAll(p => p.Name == name);
        store.Save(data, now);
        return new JsonObject().Add("ok", true).Add("message", $"Profil {name} zmazaný.");
    }

    /// <summary>Gives a group its profile ("" = the group is not published by this console).</summary>
    public static JsonObject SetGroup(string folder, byte[] body, DateTimeOffset now)
    {
        var f = new JsonFields(JsonReader.Parse(Encoding.UTF8.GetString(body), JsonSyntax.Strict), "skupina");
        var group = f.String("group").Trim();
        var profile = f.String("profile").Trim();
        f.End();
        if (!PcRules.GroupName(group))
        {
            return Fail("Neznáma skupina.");
        }

        var store = Store(folder);
        var (data, _) = store.Load();
        if (profile.Length > 0 && data.FindProfile(profile) is null)
        {
            return Fail($"Profil {profile} neexistuje.");
        }

        data.Groups.RemoveAll(g => g.Name == group);
        if (profile.Length > 0)
        {
            data.Groups.Add(new ClassroomGroup { Name = group, DefaultProfile = profile });
        }

        store.Save(data, now);
        return new JsonObject().Add("ok", true).Add("message", profile.Length > 0 ? $"Skupina {group}: profil {profile}." : $"Skupina {group}: bez profilu.");
    }

    /// <summary>The click on Zverejniť: every profile put together again from the library (a new version only when its
    /// content changed), signed by this console, the plans signed, everything handed to the centre.</summary>
    public static JsonObject Publish(string folder, (Uri Address, string Pin) server, SigningKey key, string console, DateTimeOffset now)
    {
        var store = Store(folder);
        var (data, _) = store.Load();
        if (data.Groups.Count == 0)
        {
            return Fail("Žiadna skupina nemá profil – vyber ho pri skupine.");
        }

        var lines = new List<string>();
        var library = Library(folder).ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var profile in data.Profiles.Where(p => p.Parts.Count > 0))
        {
            var missing = profile.Parts.Where(p => !library.ContainsKey(p)).ToList();
            if (missing.Count > 0)
            {
                return Fail($"Profil {profile.Name}: v knižnici chýba {string.Join(", ", missing)}.");
            }

            var file = store.RecipeFile(profile);
            var old = File.Exists(file) ? RecipeText.Load(file) : null;
            var composed = Compose(profile, library, old?.Version ?? 1);
            if (old is not null && RecipeText.Fingerprint(composed) == RecipeText.Fingerprint(old) && File.Exists(RecipeSigning.SignatureFileOf(file)))
            {
                continue; // nothing changed: the same recipe, the same signature
            }

            if (old is not null)
            {
                composed = Compose(profile, library, old.Version + 1);
            }

            if (RecipeCheck.Problems(composed) is { Count: > 0 } problems)
            {
                return new JsonObject().Add("ok", false).Add("message", $"Profil {profile.Name} sa nedá zložiť:").Add("lines", JsonArray.Of(problems, p => p));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, RecipeText.ToJson(composed));
            File.WriteAllText(RecipeSigning.SignatureFileOf(file), RecipeSigning.ToJson(RecipeSigning.Sign(composed, key, console, now)));
            lines.Add($"Profil {profile.Name} v{composed.Version}: {composed.Elements.Count} prvkov, podpísaný konzolou {console}.");
        }

        var ok = Commands.PublishWith(data, store, key, console, server, now, lines);
        return new JsonObject().Add("ok", ok).Add("message", ok ? "Zverejnené – PC si profil vezmú do 5 minút." : "Zverejnenie sa nepodarilo.").Add("lines", JsonArray.Of(lines, l => l));
    }

    private static Recipe Compose(ProfileRecipe profile, Dictionary<string, LibraryRecipe> library, int version) =>
        ProfileComposer.Compose(profile.Name, profile.Parts.Select(p => (p, RecipeText.FromJson(RecipeText.ToJson(library[p].Recipe)))).ToList(), version);

    private static JsonObject Fail(string message) => new JsonObject().Add("ok", false).Add("message", message);
}
