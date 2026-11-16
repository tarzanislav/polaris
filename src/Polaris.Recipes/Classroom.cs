using System.Globalization;
using System.Text;
using Polaris.Core;
using Polaris.Json;

namespace Polaris.Recipes;

/// <summary>A group of PCs of the same hardware with the profile it runs outside the timetable.</summary>
public sealed class ClassroomGroup
{
    public string Name { get; set; } = "";

    public string DefaultProfile { get; set; } = "";
}

/// <summary>A PC as the centre knows it: a name given at the centre (nothing about the hardware) and its group.</summary>
public sealed class ClassroomPc
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string Group { get; set; } = "";
}

/// <summary>A profile = a named recipe (e.g. KANCELARSKY → recepty/kancelarsky.json). The timetable says which profile a
/// group runs in which lesson.</summary>
public sealed class ProfileRecipe
{
    public string Name { get; set; } = "";

    /// <summary>The recipe file: relative to the folder of the centre's settings, or a full path.</summary>
    public string File { get; set; } = "";

    public string Description { get; set; } = "";
}

/// <summary>Everything the centre keeps about the classroom: groups, PCs, profiles (recipes) and the timetable. One file
/// in the own JSON, read strictly.</summary>
public sealed class Classroom
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    public DateTimeOffset SavedUtc { get; set; }

    public List<ClassroomGroup> Groups { get; set; } = [];

    public List<ClassroomPc> Pcs { get; set; } = [];

    public List<ProfileRecipe> Profiles { get; set; } = [];

    public Timetable Timetable { get; set; } = new();

    public ProfileRecipe? FindProfile(string name) => Profiles.FirstOrDefault(p => p.Name == name);

    public IEnumerable<ClassroomPc> InGroup(string group) => Pcs.Where(p => p.Group == group);
}

/// <summary>The written form of <see cref="Classroom"/>.</summary>
public static class ClassroomFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string ToJson(Classroom c) => JsonText.Readable(new JsonObject()
        .Add("schema", c.Schema)
        .Add("savedUtc", c.SavedUtc.ToUniversalTime().ToString("O", Inv))
        .Add("groups", JsonArray.Of(c.Groups, g => new JsonObject().Add("name", g.Name).Add("defaultProfile", g.DefaultProfile)))
        .Add("pcs", JsonArray.Of(c.Pcs, p => new JsonObject().Add("id", p.Id.ToString("D")).Add("name", p.Name).Add("group", p.Group)))
        .Add("profiles", JsonArray.Of(c.Profiles, p => new JsonObject().Add("name", p.Name).Add("file", p.File).AddIf(p.Description.Length > 0, "description", p.Description)))
        .Add("timetable", Timetable(c.Timetable)));

    public static Classroom FromJson(string json)
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json, JsonSyntax.Human), "nastavenia centra");
            var schema = f.Int("schema", Classroom.CurrentSchema);
            if (schema > Classroom.CurrentSchema)
            {
                throw new InvalidDataException($"Súbor nastavení je z novšieho programu (schéma {schema}, tento program pozná {Classroom.CurrentSchema}).");
            }

            var c = new Classroom
            {
                Schema = schema,
                SavedUtc = f.Time("savedUtc"),
                Groups = f.Array("groups").Select(n => Read(n, "skupina", g => new ClassroomGroup { Name = g.String("name"), DefaultProfile = g.String("defaultProfile") })).ToList(),
                Pcs = f.Array("pcs").Select(n => Read(n, "PC", p => new ClassroomPc { Id = Id(p), Name = p.String("name"), Group = p.String("group") })).ToList(),
                Profiles = f.Array("profiles").Select(n => Read(n, "profil", p => new ProfileRecipe { Name = p.String("name"), File = p.String("file"), Description = p.String("description") })).ToList(),
                Timetable = f.Fields("timetable", "rozvrh") is { } t ? ReadTimetable(t) : new Timetable(),
            };
            f.End();
            return c;
        }
        catch (JsonError e)
        {
            throw new InvalidDataException("Súbor nastavení sa nedá prečítať: " + e.Message, e);
        }
    }

    private static T Read<T>(JsonNode node, string what, Func<JsonFields, T> map)
    {
        var f = new JsonFields(node, what);
        var value = map(f);
        f.End();
        return value;
    }

    private static Guid Id(JsonFields f)
    {
        var text = f.String("id");
        return Guid.TryParse(text, out var id) ? id : throw JsonError.At(f.Node("id") ?? f.Object, $"„{text}“ nie je platný identifikátor PC.");
    }

    internal static JsonObject Timetable(Timetable t) => new JsonObject()
        .Add("switchMinutesBefore", t.SwitchMinutesBefore)
        .Add("periods", JsonArray.Of(t.Periods, p => new JsonObject().Add("number", p.Number).Add("start", Clock(p.Start)).Add("end", Clock(p.End))))
        .Add("lessons", JsonArray.Of(t.Lessons, l => new JsonObject().Add("weekday", l.Weekday).Add("period", l.Period).Add("group", l.Group).Add("profile", l.Profile)))
        .Add("overrides", JsonArray.Of(t.Overrides, o => new JsonObject().Add("date", Day(o.Date)).Add("period", o.Period).Add("group", o.Group).Add("profile", o.Profile)))
        .Add("daysOff", JsonArray.Of(t.DaysOff.Order(), d => Day(d)));

    internal static Timetable ReadTimetable(JsonFields f)
    {
        var t = new Timetable
        {
            SwitchMinutesBefore = (int)f.Long("switchMinutesBefore", 3, 0, 60),
            Periods = f.Array("periods").Select(n => Read(n, "hodina zvonenia", p => new Period((int)p.Long("number", 0, 0, 20), Clock(p, "start"), Clock(p, "end")))).ToList(),
            Lessons = f.Array("lessons").Select(n => Read(n, "hodina rozvrhu", l => new Lesson((int)l.Long("weekday", 1, 1, 7), (int)l.Long("period", 0, 0, 20), l.String("group"), l.String("profile")))).ToList(),
            Overrides = f.Array("overrides").Select(n => Read(n, "jednorazová zmena", o => new Override(Day(o, "date"), (int)o.Long("period", 0, 0, 20), o.String("group"), o.String("profile")))).ToList(),
        };
        foreach (var d in f.Array("daysOff"))
        {
            t.DaysOff.Add(d is JsonString s && DateOnly.TryParseExact(s.Value, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var day)
                ? day
                : throw JsonError.At(d, "voľný deň má byť dátum v tvare 2026-12-24."));
        }

        f.End();
        return t;
    }

    private static string Clock(TimeOnly t) => t.ToString("HH:mm", Inv);

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    private static TimeOnly Clock(JsonFields f, string name)
    {
        var text = f.String(name);
        return TimeOnly.TryParseExact(text, "HH:mm", Inv, DateTimeStyles.None, out var t)
            ? t
            : throw JsonError.At(f.Node(name) ?? f.Object, $"čas „{text}“ má byť v tvare 08:00.");
    }

    private static DateOnly Day(JsonFields f, string name)
    {
        var text = f.String(name);
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d)
            ? d
            : throw JsonError.At(f.Node(name) ?? f.Object, $"dátum „{text}“ má byť v tvare 2026-11-16.");
    }
}

/// <summary>Saves and loads the centre's settings. A save writes a new file first and only then replaces the old one, so
/// a power loss leaves the old or the new file, never half of one. The previous file is kept as a numbered backup (the
/// newest ten); a damaged file falls back to the newest backup that reads well, and says so.</summary>
public sealed class ClassroomStore(string file)
{
    public const int KeepBackups = 10;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public string File { get; } = file;

    public string Folder => Path.GetDirectoryName(Path.GetFullPath(File))!;

    public string BackupDirectory => Path.Combine(Folder, "zalohy");

    public (Classroom Data, string? Note) Load()
    {
        if (!System.IO.File.Exists(File))
        {
            return (new Classroom(), null);
        }

        try
        {
            return (ClassroomFormat.FromJson(System.IO.File.ReadAllText(File, Utf8)), null);
        }
        catch (InvalidDataException e) when (e.InnerException is JsonError)
        {
            foreach (var backup in Backups().Reverse())
            {
                try
                {
                    var data = ClassroomFormat.FromJson(System.IO.File.ReadAllText(backup, Utf8));
                    return (data, $"Súbor nastavení je poškodený ({e.Message}); načítaná záloha {Path.GetFileName(backup)}.");
                }
                catch (InvalidDataException)
                {
                    // try an older one
                }
            }

            throw new InvalidDataException("Súbor nastavení je poškodený a žiadna záloha sa nedá prečítať. " + e.Message, e);
        }
    }

    public void Save(Classroom data, DateTimeOffset now)
    {
        data.Schema = Classroom.CurrentSchema;
        data.SavedUtc = now;
        Directory.CreateDirectory(Folder);
        if (System.IO.File.Exists(File))
        {
            Directory.CreateDirectory(BackupDirectory);
            System.IO.File.Copy(File, Path.Combine(BackupDirectory, $"centrum-zaloha-{NextBackupNumber():D4}.json"));
            foreach (var old in Backups().SkipLast(KeepBackups))
            {
                System.IO.File.Delete(old);
            }
        }

        var temp = File + ".novy";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(Utf8.GetBytes(ClassroomFormat.ToJson(data)));
            stream.Flush(flushToDisk: true);
        }

        System.IO.File.Move(temp, File, overwrite: true);
    }

    /// <summary>The full path of a profile's recipe.</summary>
    public string RecipeFile(ProfileRecipe p) => Path.GetFullPath(Path.Combine(Folder, p.File));

    /// <summary>How a recipe file is written into the settings: relative when it lies in the centre's folder.</summary>
    public string Relative(string recipeFile)
    {
        var full = Path.GetFullPath(recipeFile);
        var relative = Path.GetRelativePath(Folder, full);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative.Replace('\\', '/');
    }

    public IEnumerable<string> Backups() =>
        Directory.Exists(BackupDirectory)
            ? Directory.GetFiles(BackupDirectory, "centrum-zaloha-*.json").OrderBy(f => f, StringComparer.Ordinal)
            : [];

    private int NextBackupNumber()
    {
        var last = Backups().LastOrDefault();
        if (last is null)
        {
            return 1;
        }

        var name = Path.GetFileNameWithoutExtension(last);
        return int.TryParse(name.AsSpan(name.LastIndexOf('-') + 1), out var n) ? n + 1 : 1;
    }
}
