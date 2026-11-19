using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Polaris.Json;

namespace Polaris.Recipes;

/// <summary>A driver's setup file (INF) read by own code: sections, values with %strings%, the hardware the driver is
/// for. Lenient like Windows itself – an odd line is skipped, never a reason to fail.</summary>
public sealed class InfFile
{
    private readonly Dictionary<string, List<string>> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);

    private InfFile()
    {
    }

    public string? Class => Value("Version", "Class");

    public string? Provider => Value("Version", "Provider");

    /// <summary>The catalog with the signature: the one for 64-bit Windows, or the common one.</summary>
    public string? Catalog => Value("Version", "CatalogFile.NTamd64") ?? Value("Version", "CatalogFile.NT") ?? Value("Version", "CatalogFile");

    /// <summary>DriverVer = date, version (e.g. "07/18/2016,11.0.0.1166").</summary>
    public (string Date, string Version) DriverVer
    {
        get
        {
            var parts = Split(Value("Version", "DriverVer") ?? "");
            return (parts.Count > 0 ? parts[0] : "", parts.Count > 1 ? parts[1] : "");
        }
    }

    /// <summary>Text of an INF in any of the encodings Windows accepts: UTF-16 with its mark, UTF-8, or the old code
    /// page (read as Latin-1 – the names the agent needs are plain letters).</summary>
    public static InfFile Load(byte[] bytes)
    {
        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        return Parse(text);
    }

    public static InfFile Parse(string text)
    {
        var inf = new InfFile();
        List<string>? current = null;
        var pending = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw.TrimEnd('\r')).Trim();
            if (line.EndsWith('\\'))
            {
                pending.Append(line.AsSpan(0, line.Length - 1)).Append(' ');
                continue;
            }

            if (pending.Length > 0)
            {
                line = pending.Append(line).ToString().Trim();
                pending.Clear();
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.IndexOf(']') is var end and > 1)
            {
                var name = line[1..end].Trim();
                if (!inf._sections.TryGetValue(name, out current))
                {
                    current = [];
                    inf._sections[name] = current;
                }

                continue;
            }

            current?.Add(line);
        }

        // [Strings] first, then the localized [Strings.xxxx] only for keys the plain one lacks
        foreach (var section in inf._sections.Keys.Where(k => k.Equals("Strings", StringComparison.OrdinalIgnoreCase))
                     .Concat(inf._sections.Keys.Where(k => k.StartsWith("Strings.", StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var line in inf._sections[section])
            {
                if (KeyValue(line) is ({ } key, { } value))
                {
                    inf._strings.TryAdd(key, Unquote(value));
                }
            }
        }

        return inf;
    }

    /// <summary>The value of a key in a section, %strings% replaced; null when not there.</summary>
    public string? Value(string section, string key)
    {
        if (!_sections.TryGetValue(section, out var lines))
        {
            return null;
        }

        foreach (var line in lines)
        {
            if (KeyValue(line) is ({ } k, { } v) && string.Equals(Expand(k), key, StringComparison.OrdinalIgnoreCase))
            {
                return Expand(Unquote(v));
            }
        }

        return null;
    }

    /// <summary>The hardware ids (and compatible ids) of the devices this driver is for on 64-bit Windows, in capitals.
    /// From [Manufacturer] to its model sections: plain, NT, NTamd64 and NTamd64 with a version.</summary>
    public IReadOnlyList<string> HardwareIds()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        if (!_sections.TryGetValue("Manufacturer", out var makers))
        {
            return [];
        }

        foreach (var line in makers)
        {
            var (_, value) = KeyValue(line);
            var parts = Split(value ?? line);
            if (parts.Count == 0)
            {
                continue;
            }

            var models = Expand(parts[0]);
            // with decorations Windows uses only the decorated sections; none for 64-bit = not for this Windows
            IEnumerable<string> sections = parts.Count == 1
                ? [models]
                : parts.Skip(1).Select(Expand).Where(ForAmd64).Select(d => models + "." + d);
            foreach (var section in sections)
            {
                if (!_sections.TryGetValue(section, out var lines))
                {
                    continue;
                }

                foreach (var model in lines)
                {
                    var (_, ids0) = KeyValue(model);
                    foreach (var id in Split(ids0 ?? "").Skip(1).Select(Expand))
                    {
                        if (id.Length > 0)
                        {
                            ids.Add(id.ToUpperInvariant());
                        }
                    }
                }
            }
        }

        return [.. ids];
    }

    private static bool ForAmd64(string decoration)
    {
        var d = decoration.Trim();
        return d.Equals("NT", StringComparison.OrdinalIgnoreCase)
            || d.StartsWith("NT.", StringComparison.OrdinalIgnoreCase)
            || d.StartsWith("NTamd64", StringComparison.OrdinalIgnoreCase);
    }

    private string Expand(string text)
    {
        if (!text.Contains('%'))
        {
            return text.Trim();
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var close = text[i] == '%' ? text.IndexOf('%', i + 1) : -1;
            if (close < 0)
            {
                sb.Append(text[i++]);
                continue;
            }

            var key = text[(i + 1)..close];
            sb.Append(key.Length == 0 ? "%" : _strings.TryGetValue(key, out var s) ? s : text[i..(close + 1)]);
            i = close + 1;
        }

        return sb.ToString().Trim();
    }

    private static (string? Key, string? Value) KeyValue(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == '=' && !quoted)
            {
                return (Unquote(line[..i].Trim()), line[(i + 1)..].Trim());
            }
        }

        return (null, null);
    }

    /// <summary>A list separated by commas; quotes keep commas inside.</summary>
    private static List<string> Split(string text)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                list.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        list.Add(sb.ToString().Trim());
        return list;
    }

    private static string Unquote(string text)
    {
        var t = text.Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : t;
    }

    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ';' && !quoted)
            {
                return line[..i];
            }
        }

        return line;
    }
}

/// <summary>One file of a driver package in a backup, pinned by its fingerprint.</summary>
public sealed record DriverFile(string Path, string Sha256, long Size);

/// <summary>A device of the reference PC the package served (for people and for matching when the INF names none).</summary>
public sealed record DriverDevice(string Name, IReadOnlyList<string> HardwareIds);

/// <summary>One driver package in a backup: its folder, its INF and what it is for.</summary>
public sealed class DriverPackageEntry
{
    /// <summary>Folder inside the backup (e.g. "003-heci").</summary>
    public string Folder { get; set; } = "";

    /// <summary>The INF's file name inside the folder.</summary>
    public string Inf { get; set; } = "";

    public string Class { get; set; } = "";

    public string Provider { get; set; } = "";

    public string Version { get; set; } = "";

    public string Date { get; set; } = "";

    /// <summary>The catalog with the signature, inside the folder.</summary>
    public string Catalog { get; set; } = "";

    /// <summary>Who signed the catalog when the backup was made.</summary>
    public string Signer { get; set; } = "";

    /// <summary>Hardware ids from the INF (64-bit Windows).</summary>
    public List<string> SupportedIds { get; set; } = [];

    public List<DriverDevice> Devices { get; set; } = [];

    public List<DriverFile> Files { get; set; } = [];

    public string Title => $"{Provider} {Class} {Version}".Trim();
}

/// <summary>The drivers of one hardware build, taken from a reference PC: the folder holds ovladace.json and one folder
/// per driver package. Every file is pinned by SHA-256, so a changed or swapped file is never installed.</summary>
public sealed class DriverBackup
{
    public const int CurrentSchema = 1;
    public const string FileName = "ovladace.json";

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>The name of the hardware build: "Maker Model - Windows 11 Pro" from the firmware and Windows, or what the
    /// administrator gave (--meno).</summary>
    public string Name { get; set; } = "";

    /// <summary>Maker and model of the PC as the firmware says it (for finding the backup of a PC of the same build).</summary>
    public string Hardware { get; set; } = "";

    /// <summary>The edition of Windows the drivers were taken from (e.g. Windows 11 Pro).</summary>
    public string Edition { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>The Windows the drivers were taken from (e.g. 10.0.26200).</summary>
    public string Windows { get; set; } = "";

    public List<DriverPackageEntry> Packages { get; set; } = [];
}

public static partial class DriverBackupFormat
{
    public static string ToJson(DriverBackup b) => JsonText.Readable(Write(b));

    public static string Fingerprint(DriverBackup b) => JsonText.Fingerprint(Write(b));

    public static JsonObject Write(DriverBackup b) => new JsonObject()
        .Add("schema", b.Schema)
        .Add("name", b.Name)
        .Add("createdUtc", b.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        .AddIf(b.Windows.Length > 0, "windows", b.Windows)
        .AddIf(b.Hardware.Length > 0, "hardware", b.Hardware)
        .AddIf(b.Edition.Length > 0, "edition", b.Edition)
        .Add("packages", JsonArray.Of(b.Packages, p => new JsonObject()
            .Add("folder", p.Folder)
            .Add("inf", p.Inf)
            .AddIf(p.Class.Length > 0, "class", p.Class)
            .AddIf(p.Provider.Length > 0, "provider", p.Provider)
            .AddIf(p.Version.Length > 0, "version", p.Version)
            .AddIf(p.Date.Length > 0, "date", p.Date)
            .AddIf(p.Catalog.Length > 0, "catalog", p.Catalog)
            .AddIf(p.Signer.Length > 0, "signer", p.Signer)
            .AddIf(p.SupportedIds.Count > 0, "supportedIds", JsonArray.Of(p.SupportedIds, x => x))
            .AddIf(p.Devices.Count > 0, "devices", JsonArray.Of(p.Devices, d => new JsonObject()
                .Add("name", d.Name)
                .Add("hardwareIds", JsonArray.Of(d.HardwareIds, x => x))))
            .Add("files", JsonArray.Of(p.Files, f => new JsonObject().Add("path", f.Path).Add("sha256", f.Sha256).Add("size", f.Size)))));

    public static DriverBackup Load(string folder) =>
        FromJson(File.ReadAllText(Path.Combine(folder, DriverBackup.FileName), Encoding.UTF8), Path.Combine(folder, DriverBackup.FileName));

    public static DriverBackup FromJson(string json, string what = "záloha ovládačov")
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json), what);
            var schema = f.Int("schema", DriverBackup.CurrentSchema);
            if (schema != DriverBackup.CurrentSchema)
            {
                throw new InvalidDataException($"{what}: neznáma schéma {schema}.");
            }

            var b = new DriverBackup { Schema = schema, Name = f.String("name"), CreatedUtc = f.Time("createdUtc"), Windows = f.String("windows"), Hardware = f.String("hardware"), Edition = f.String("edition") };
            foreach (var node in f.Array("packages"))
            {
                var p = new JsonFields(node, $"balík ovládača ({what})");
                var entry = new DriverPackageEntry
                {
                    Folder = p.String("folder"),
                    Inf = p.String("inf"),
                    Class = p.String("class"),
                    Provider = p.String("provider"),
                    Version = p.String("version"),
                    Date = p.String("date"),
                    Catalog = p.String("catalog"),
                    Signer = p.String("signer"),
                    SupportedIds = p.Strings("supportedIds"),
                };
                foreach (var d in p.Array("devices"))
                {
                    var df = new JsonFields(d, $"zariadenie ({what})");
                    entry.Devices.Add(new DriverDevice(df.String("name"), df.Strings("hardwareIds")));
                    df.End();
                }

                foreach (var file in p.Array("files"))
                {
                    var ff = new JsonFields(file, $"súbor ({what})");
                    entry.Files.Add(new DriverFile(ff.String("path"), ff.String("sha256"), ff.Long("size", -1, 0)));
                    ff.End();
                }

                p.End();
                b.Packages.Add(entry);
            }

            f.End();
            return b;
        }
        catch (JsonError e)
        {
            throw new InvalidDataException($"{what}: {e.Message}", e);
        }
    }

    /// <summary>Problems of the description alone: names, paths, fingerprints.</summary>
    public static List<string> Problems(DriverBackup b)
    {
        var problems = new List<string>();
        if (b.Name.Length == 0)
        {
            problems.Add("záloha nemá meno zostavy.");
        }

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in b.Packages)
        {
            if (!FolderPattern().IsMatch(p.Folder) || !folders.Add(p.Folder))
            {
                problems.Add($"priečinok balíka „{p.Folder}“ je neplatný alebo dvakrát.");
                continue;
            }

            if (!p.Files.Any(f => string.Equals(f.Path, p.Inf, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{p.Folder}: súbor {p.Inf} nie je v zozname súborov.");
            }

            if (p.Catalog.Length > 0 && !p.Files.Any(f => string.Equals(f.Path, p.Catalog, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{p.Folder}: katalóg {p.Catalog} nie je v zozname súborov.");
            }

            foreach (var f in p.Files)
            {
                if (!SafePath(f.Path))
                {
                    problems.Add($"{p.Folder}: cesta „{f.Path}“ nesmie ísť mimo priečinka balíka.");
                }

                if (!Sha256Pattern().IsMatch(f.Sha256) || f.Size < 0)
                {
                    problems.Add($"{p.Folder}: súbor {f.Path} nemá platný odtlačok.");
                }
            }
        }

        return problems;
    }

    /// <summary>Every file of the backup checked against the description: there, the same size, the same fingerprint.</summary>
    public static List<string> VerifyFiles(string folder, DriverBackup b)
    {
        var problems = Problems(b);
        if (problems.Count > 0)
        {
            return problems;
        }

        foreach (var p in b.Packages)
        {
            problems.AddRange(VerifyPackage(Path.Combine(folder, p.Folder), p));
        }

        return problems;
    }

    public static IEnumerable<string> VerifyPackage(string packageFolder, DriverPackageEntry p)
    {
        foreach (var f in p.Files)
        {
            var path = Path.Combine(packageFolder, f.Path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                yield return $"{p.Folder}: chýba súbor {f.Path}.";
            }
            else if (new FileInfo(path).Length != f.Size || RecipeText.FileFingerprint(path) != f.Sha256)
            {
                yield return $"{p.Folder}: súbor {f.Path} bol zmenený (iný odtlačok).";
            }
        }
    }

    /// <summary>Does the package serve some device of this PC? By the INF's ids, or – when the INF named none – by the
    /// devices of the reference PC.</summary>
    public static bool Serves(DriverPackageEntry p, IEnumerable<string> presentIds)
    {
        var present = new HashSet<string>(presentIds.Select(i => i.ToUpperInvariant()), StringComparer.Ordinal);
        IEnumerable<string> wanted = p.SupportedIds.Count > 0 ? p.SupportedIds : p.Devices.SelectMany(d => d.HardwareIds);
        return wanted.Any(id => present.Contains(id.ToUpperInvariant()));
    }

    /// <summary>A folder name for a package: number and the INF's name in plain small letters.</summary>
    public static string FolderName(int index, string inf)
    {
        var sb = new StringBuilder();
        foreach (var c in Path.GetFileNameWithoutExtension(inf).ToLowerInvariant())
        {
            var plain = char.IsAsciiLetterOrDigit(c) ? c : '-';
            if (plain != '-' || (sb.Length > 0 && sb[^1] != '-'))
            {
                sb.Append(plain);
            }
        }

        var name = sb.ToString().Trim('-');
        return $"{index:000}-{(name.Length == 0 ? "ovladac" : name[..Math.Min(name.Length, 40)])}";
    }

    private static bool SafePath(string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':'))
        {
            return false;
        }

        return path.Replace('\\', '/').Split('/').All(part => part.Length > 0 && part != "." && part != "..");
    }

    [GeneratedRegex("^[0-9]{3}-[a-z0-9-]{1,40}$")]
    private static partial Regex FolderPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();
}
