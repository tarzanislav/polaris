using Polaris.Json;

namespace Polaris.Recipes;

/// <summary>How recipes and their parts look as JSON – written by hand, field by field, so the format is exactly what
/// docs/recept.md says and nothing else. Reading refuses unknown fields (a typo never passes silently); writing leaves
/// out every field that has its default value, so a field added in a later version with a default never changes the
/// fingerprint of an older recipe.</summary>
public static class RecipeFormat
{
    public static readonly IReadOnlyList<string> Types =
        ["program", "registry", "bookmarks", "service", "feature", "user-registry", "keyboards", "session", "sandbox", "store", "computer", "updates", "power", "sounds", "startup"];

    public static Recipe Read(JsonNode root)
    {
        var f = new JsonFields(root, "recept");
        var schema = f.Int("schema", Recipe.CurrentSchema);
        if (schema > Recipe.CurrentSchema)
        {
            throw new InvalidDataException($"Recept je z novšieho programu (schéma {schema}, tento program pozná {Recipe.CurrentSchema}).");
        }

        if (schema < 1)
        {
            throw new InvalidDataException("Recept nemá platné číslo schémy.");
        }

        var recipe = new Recipe
        {
            Schema = schema,
            Name = f.String("name"),
            Version = f.Int("version", 0),
            Note = f.String("note"),
        };
        var elements = f.Array("elements");
        for (var i = 0; i < elements.Count; i++)
        {
            recipe.Elements.Add(ReadElement(elements[i], i));
        }

        f.End();
        return recipe;
    }

    public static JsonObject Write(Recipe r) => new JsonObject()
        .Add("schema", r.Schema)
        .AddIf(r.Name.Length > 0, "name", r.Name)
        .AddIf(r.Version != 0, "version", r.Version)
        .AddIf(r.Note.Length > 0, "note", r.Note)
        .Add("elements", JsonArray.Of(r.Elements, WriteElement));

    public static Element ReadElement(JsonNode node, int index)
    {
        var id = node is JsonObject o && o["id"] is JsonString s && s.Value.Length > 0 ? s.Value : null;
        var f = new JsonFields(node, id is null ? $"prvok č. {index + 1}" : $"prvok {id}");
        var typeNode = f.Node("type") ?? throw JsonError.At(node, $"{f.What} nemá \"type\" ({string.Join(", ", Types)}).");
        var type = f.String("type");
        Element e = type switch
        {
            "program" => Program(f),
            "registry" => new RegistryValueElement { Path = f.String("path"), Name = f.String("name"), Kind = f.Enum("kind", RegistryKind.String), Data = f.String("data") },
            "bookmarks" => Bookmarks(f),
            "service" => new ServiceElement { Name = f.String("name"), Start = f.Enum("start", ServiceStart.Manual), Running = f.OptionalBool("running"), PhysicalOnly = f.Bool("physicalOnly", false) },
            "feature" => new FeatureElement { Name = f.String("name") },
            "user-registry" => new UserRegistryElement { Path = f.String("path"), Name = f.String("name"), Kind = f.Enum("kind", RegistryKind.String), Data = f.String("data") },
            "keyboards" => new KeyboardsElement { Languages = f.Strings("languages"), Layouts = f.Map("layouts") },
            "session" => new SessionElement { Account = f.String("account"), Clean = f.Enum("clean", SessionCleaning.AtSignOut) },
            "sandbox" => Sandbox(f),
            "store" => Store(f),
            "computer" => new ComputerElement { Workgroup = f.OptionalString("workgroup"), TimeZone = f.OptionalString("timeZone") },
            "updates" => Updates(f),
            "power" => Power(f),
            "sounds" => new SoundsElement { Scheme = f.Enum("scheme", SoundScheme.None) },
            "startup" => new StartupElement { Allow = f.Strings("allow") },
            _ => throw JsonError.At(typeNode, $"neznámy typ prvku „{type}“ (možné: {string.Join(", ", Types)})."),
        };
        e.Id = f.String("id");
        e.Present = f.Bool("present", true);
        e.Note = f.OptionalString("note");
        f.End();
        return e;
    }

    public static JsonObject WriteElement(Element e)
    {
        var o = new JsonObject().Add("type", TypeOf(e)).AddIf(e.Id.Length > 0, "id", e.Id);
        switch (e)
        {
            case ProgramElement p:
                WriteProgram(o, p);
                break;
            case RegistryValueElement r:
                o.Add("path", r.Path).AddIf(r.Name.Length > 0, "name", r.Name).AddIf(r.Kind != RegistryKind.String, "kind", r.Kind.ToString()).AddIf(r.Data.Length > 0, "data", r.Data);
                break;
            case BookmarksElement b:
                o.AddIf(!b.Browsers.SequenceEqual(new BookmarksElement().Browsers), "browsers", JsonArray.Of(b.Browsers, x => x.ToString()))
                    .AddIf(b.Folder.Length > 0, "folder", b.Folder)
                    .AddIf(!b.ShowBar, "showBar", false)
                    .Add("items", JsonArray.Of(b.Items, WriteBookmark));
                break;
            case ServiceElement s:
                o.Add("name", s.Name).AddIf(s.Start != ServiceStart.Manual, "start", s.Start.ToString()).AddIf("running", s.Running is { } running ? (JsonNode)running : null)
                    .AddIf(s.PhysicalOnly, "physicalOnly", true);
                break;
            case FeatureElement x:
                o.Add("name", x.Name);
                break;
            case UserRegistryElement u:
                o.Add("path", u.Path).AddIf(u.Name.Length > 0, "name", u.Name).AddIf(u.Kind != RegistryKind.String, "kind", u.Kind.ToString()).AddIf(u.Data.Length > 0, "data", u.Data);
                break;
            case KeyboardsElement k:
                o.Add("languages", JsonArray.Of(k.Languages, x => x));
                if (k.Layouts is { Count: > 0 } layouts)
                {
                    var map = new JsonObject();
                    foreach (var (language, layout) in layouts)
                    {
                        map.Add(language, layout);
                    }

                    o.Add("layouts", map);
                }

                break;
            case SessionElement s:
                o.Add("account", s.Account).AddIf(s.Clean != SessionCleaning.AtSignOut, "clean", s.Clean.ToString());
                break;
            case SandboxElement s:
                WriteSandbox(o, s);
                break;
            case StoreElement s:
                o.Add("accounts", JsonArray.Of(s.Accounts, x => x))
                    .AddIf(s.MaxPerSession != new StoreElement().MaxPerSession, "maxPerSession", s.MaxPerSession)
                    .Add("items", JsonArray.Of(s.Items, i => new JsonObject()
                        .Add("id", i.Id)
                        .Add("title", i.Title)
                        .AddIf(i.Description.Length > 0, "description", i.Description)
                        .Add("program", WriteProgram(new JsonObject().AddIf(i.Program.Id.Length > 0, "id", i.Program.Id), i.Program))));
                break;
            case ComputerElement c:
                o.AddIf("workgroup", Text(c.Workgroup)).AddIf("timeZone", Text(c.TimeZone));
                break;
            case UpdatesElement u:
                o.AddIf("activeHoursStart", Number(u.ActiveHoursStart))
                    .AddIf("activeHoursEnd", Number(u.ActiveHoursEnd))
                    .AddIf("deferFeatureDays", Number(u.DeferFeatureDays))
                    .AddIf("deferQualityDays", Number(u.DeferQualityDays))
                    .AddIf("targetRelease", Text(u.TargetRelease))
                    .AddIf(u.ExcludeDrivers, "excludeDrivers", true);
                break;
            case SoundsElement z:
                o.Add("scheme", z.Scheme.ToString());
                break;
            case StartupElement t:
                o.Add("allow", JsonArray.Of(t.Allow, x => x));
                break;
            case PowerElement w:
                o.AddIf("sleepMinutes", Number(w.SleepMinutes))
                    .AddIf("displayMinutes", Number(w.DisplayMinutes))
                    .AddIf("hibernateMinutes", Number(w.HibernateMinutes))
                    .AddIf("powerButton", w.PowerButton is { } button ? (JsonNode)button.ToString() : null)
                    .AddIf("lidClose", w.LidClose is { } lid ? (JsonNode)lid.ToString() : null)
                    .AddIf("wakePassword", w.WakePassword is { } password ? (JsonNode)password : null)
                    .AddIf("fastStartup", w.FastStartup is { } fast ? (JsonNode)fast : null)
                    .AddIf("hibernation", w.Hibernation is { } hibernation ? (JsonNode)hibernation : null)
                    .AddIf(w.NetworkFullPower, "networkFullPower", true);
                break;
            default:
                throw new ArgumentException("Neznámy druh prvku.", nameof(e));
        }

        return o.AddIf(!e.Present, "present", false).AddIf(e.Note is { Length: > 0 }, "note", e.Note ?? "");
    }

    public static string TypeOf(Element e) => e switch
    {
        ProgramElement => "program",
        RegistryValueElement => "registry",
        BookmarksElement => "bookmarks",
        ServiceElement => "service",
        FeatureElement => "feature",
        UserRegistryElement => "user-registry",
        KeyboardsElement => "keyboards",
        SessionElement => "session",
        SandboxElement => "sandbox",
        StoreElement => "store",
        ComputerElement => "computer",
        UpdatesElement => "updates",
        PowerElement => "power",
        SoundsElement => "sounds",
        StartupElement => "startup",
        _ => throw new ArgumentException("Neznámy druh prvku.", nameof(e)),
    };

    private static JsonNode? Text(string? value) => value is { Length: > 0 } text ? (JsonNode)text : null;

    private static JsonNode? Number(int? value) => value is { } v ? (JsonNode)v : null;

    private static int? OptionalInt(JsonFields f, string name) => f.Has(name) ? f.Int(name, 0) : null;

    private static UpdatesElement Updates(JsonFields f) => new()
    {
        ActiveHoursStart = OptionalInt(f, "activeHoursStart"),
        ActiveHoursEnd = OptionalInt(f, "activeHoursEnd"),
        DeferFeatureDays = OptionalInt(f, "deferFeatureDays"),
        DeferQualityDays = OptionalInt(f, "deferQualityDays"),
        TargetRelease = f.OptionalString("targetRelease"),
        ExcludeDrivers = f.Bool("excludeDrivers", false),
    };

    private static PowerElement Power(JsonFields f) => new()
    {
        SleepMinutes = OptionalInt(f, "sleepMinutes"),
        DisplayMinutes = OptionalInt(f, "displayMinutes"),
        HibernateMinutes = OptionalInt(f, "hibernateMinutes"),
        PowerButton = f.OptionalEnum<PowerAction>("powerButton"),
        LidClose = f.OptionalEnum<PowerAction>("lidClose"),
        WakePassword = f.OptionalBool("wakePassword"),
        FastStartup = f.OptionalBool("fastStartup"),
        Hibernation = f.OptionalBool("hibernation"),
        NetworkFullPower = f.Bool("networkFullPower", false),
    };

    /// <summary>A program alone (in the School store): the same fields as the element, "type" may be left out.</summary>
    public static ProgramElement ReadProgram(JsonNode node, string what)
    {
        var f = new JsonFields(node, what);
        if (f.OptionalString("type") is { } type && type != "program")
        {
            throw JsonError.At(f.Node("type")!, $"{what}: tu môže byť len program.");
        }

        var p = Program(f);
        p.Id = f.String("id");
        p.Present = f.Bool("present", true);
        p.Note = f.OptionalString("note");
        f.End();
        return p;
    }

    public static JsonObject WriteProgramAlone(ProgramElement p) =>
        WriteProgram(new JsonObject().AddIf(p.Id.Length > 0, "id", p.Id), p).AddIf(!p.Present, "present", false);

    private static ProgramElement Program(JsonFields f)
    {
        var p = new ProgramElement
        {
            Winget = f.OptionalString("winget"),
            Version = f.OptionalString("version"),
            DisplayName = f.String("displayName"),
            Match = f.Enum("match", NameMatch.Exact),
        };
        if (f.Fields("package", $"balík ({f.What})") is { } k)
        {
            p.Package = new PackageRef { Id = k.String("id"), Fingerprint = k.String("fingerprint") };
            k.End();
        }

        if (f.Fields("installer", $"inštalátor ({f.What})") is { } i)
        {
            p.Installer = new InstallerSource
            {
                File = i.String("file"),
                Sha256 = i.String("sha256"),
                Arguments = i.String("arguments"),
                UninstallArguments = i.OptionalString("uninstallArguments"),
            };
            i.End();
        }

        return p;
    }

    private static JsonObject WriteProgram(JsonObject o, ProgramElement p)
    {
        o.AddIf(p.Winget is { Length: > 0 }, "winget", p.Winget ?? "").AddIf(p.Version is { Length: > 0 }, "version", p.Version ?? "");
        if (p.Installer is { } i)
        {
            o.Add("installer", new JsonObject()
                .Add("file", i.File)
                .Add("sha256", i.Sha256)
                .AddIf(i.Arguments.Length > 0, "arguments", i.Arguments)
                .AddIf(i.UninstallArguments is not null, "uninstallArguments", i.UninstallArguments ?? ""));
        }

        if (p.Package is { } k)
        {
            o.Add("package", new JsonObject().Add("id", k.Id).Add("fingerprint", k.Fingerprint));
        }

        return o.AddIf(p.DisplayName.Length > 0 || p.Package is null, "displayName", p.DisplayName).AddIf(p.Match != NameMatch.Exact, "match", p.Match.ToString());
    }

    private static BookmarksElement Bookmarks(JsonFields f)
    {
        var b = new BookmarksElement { Folder = f.String("folder"), ShowBar = f.Bool("showBar", true), Items = ReadBookmarks(f.Array("items"), f.What) };
        if (f.Has("browsers"))
        {
            b.Browsers = f.Array("browsers").Select(n => n is JsonString s && System.Enum.TryParse<Browser>(s.Value, true, out var x) && System.Enum.IsDefined(x)
                ? x
                : throw JsonError.At(n, $"{f.What}: prehliadač môže byť len Edge alebo Chrome.")).ToList();
        }

        return b;
    }

    private static List<Bookmark> ReadBookmarks(IReadOnlyList<JsonNode> nodes, string what)
    {
        var list = new List<Bookmark>(nodes.Count);
        foreach (var node in nodes)
        {
            var f = new JsonFields(node, $"záložka ({what})");
            var b = new Bookmark { Name = f.String("name"), Url = f.OptionalString("url") };
            if (f.Has("children"))
            {
                b.Children = ReadBookmarks(f.Array("children"), what);
            }

            f.End();
            list.Add(b);
        }

        return list;
    }

    private static JsonObject WriteBookmark(Bookmark b) => new JsonObject()
        .Add("name", b.Name)
        .AddIf("url", b.Url is { } url ? (JsonNode)url : null)
        .AddIf("children", b.Children is { } children ? JsonArray.Of(children, WriteBookmark) : null);

    private static SandboxElement Sandbox(JsonFields f)
    {
        var d = new SandboxElement();
        var s = new SandboxElement
        {
            Name = f.String("name"),
            Networking = f.Bool("networking", d.Networking),
            Gpu = f.Bool("gpu", d.Gpu),
            MemoryMb = f.Int("memoryMb", d.MemoryMb),
            Clipboard = f.Bool("clipboard", d.Clipboard),
            ProtectedClient = f.Bool("protectedClient", d.ProtectedClient),
            Microphone = f.Bool("microphone", d.Microphone),
            Camera = f.Bool("camera", d.Camera),
            Printer = f.Bool("printer", d.Printer),
            LogonCommand = f.OptionalString("logonCommand"),
        };
        foreach (var node in f.Array("folders"))
        {
            var m = new JsonFields(node, $"priečinok ({f.What})");
            s.Folders.Add(new MappedFolder { HostFolder = m.String("hostFolder"), SandboxFolder = m.OptionalString("sandboxFolder"), ReadOnly = m.Bool("readOnly", true) });
            m.End();
        }

        return s;
    }

    private static void WriteSandbox(JsonObject o, SandboxElement s)
    {
        var d = new SandboxElement();
        o.Add("name", s.Name)
            .AddIf(s.Networking != d.Networking, "networking", s.Networking)
            .AddIf(s.Gpu != d.Gpu, "gpu", s.Gpu)
            .AddIf(s.MemoryMb != d.MemoryMb, "memoryMb", s.MemoryMb)
            .AddIf(s.Clipboard != d.Clipboard, "clipboard", s.Clipboard)
            .AddIf(s.ProtectedClient != d.ProtectedClient, "protectedClient", s.ProtectedClient)
            .AddIf(s.Microphone != d.Microphone, "microphone", s.Microphone)
            .AddIf(s.Camera != d.Camera, "camera", s.Camera)
            .AddIf(s.Printer != d.Printer, "printer", s.Printer)
            .AddIf(s.Folders.Count > 0, "folders", JsonArray.Of(s.Folders, m => new JsonObject()
                .Add("hostFolder", m.HostFolder)
                .AddIf("sandboxFolder", m.SandboxFolder is { Length: > 0 } inside ? (JsonNode)inside : null)
                .AddIf(!m.ReadOnly, "readOnly", false)))
            .AddIf("logonCommand", s.LogonCommand is { Length: > 0 } command ? (JsonNode)command : null);
    }

    private static StoreElement Store(JsonFields f)
    {
        var s = new StoreElement { Accounts = f.Strings("accounts"), MaxPerSession = f.Int("maxPerSession", new StoreElement().MaxPerSession) };
        foreach (var node in f.Array("items"))
        {
            var i = new JsonFields(node, $"položka obchodu ({f.What})");
            var item = new StoreItem { Id = i.String("id"), Title = i.String("title"), Description = i.String("description") };
            item.Program = i.Node("program") is { } program ? ReadProgram(program, $"program položky {item.Id}") : new ProgramElement();
            if (item.Program.Id.Length == 0)
            {
                item.Program.Id = item.Id;
            }

            i.End();
            s.Items.Add(item);
        }

        return s;
    }
}
