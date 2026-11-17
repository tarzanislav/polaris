using System.Globalization;
using Polaris.Json;

namespace Polaris.Recipes;

public enum VaultKind
{
    File,
    Directory,

    /// <summary>A reparse point (the old junctions of a profile, "Application Data" and so on): kept as it is, never
    /// followed.</summary>
    Link,
}

/// <summary>One thing in a profile as it is now: path relative to the profile folder, kind, size, last write (FILETIME)
/// and attributes.</summary>
public sealed record ProfileItem(string Path, VaultKind Kind, long Size, long WriteTime, int Attributes);

/// <summary>One thing kept in the trezor: as it was, and where its backup stream lies in the archive (with its SHA-256).</summary>
public sealed record VaultEntry(string Path, VaultKind Kind, long Size, long WriteTime, int Attributes, long Offset, long Length, string Sha256)
{
    public ProfileItem Item => new(Path, Kind, Size, WriteTime, Attributes);
}

/// <summary>The description of a trezor of one account: whose profile it is (SID and the moment the profile folder was
/// made – a profile made again is another profile), from which Windows, when, and every entry in the order it was saved
/// (a folder before what is in it).</summary>
public sealed class VaultManifest
{
    public const int CurrentSchema = 1;
    public const string FileName = "trezor.json";
    public const string ArchiveName = "trezor.dat";

    public int Schema { get; set; } = CurrentSchema;

    public string Account { get; set; } = "";

    public string Sid { get; set; } = "";

    /// <summary>When the profile folder was created (FILETIME): a profile deleted and made again does not fit the trezor.</summary>
    public long ProfileCreated { get; set; }

    /// <summary>Build of Windows (e.g. 26200.7171) – after an update the trezor is old and should be saved again.</summary>
    public string Windows { get; set; } = "";

    public DateTimeOffset SavedUtc { get; set; }

    public long ArchiveLength { get; set; }

    public List<VaultEntry> Entries { get; set; } = [];
}

/// <summary>What a restore does: things to remove (the deepest first) and things to put back from the trezor (a folder
/// before its content).</summary>
public sealed record VaultPlan(IReadOnlyList<ProfileItem> Remove, IReadOnlyList<VaultEntry> Put)
{
    public bool Nothing => Remove.Count == 0 && Put.Count == 0;
}

/// <summary>The rules of the trezor, the same for the agent and the tests: what is never kept (caches – only deleted on
/// restore), how the profile now is compared with the trezor, the manifest as JSON.</summary>
public static class VaultRules
{
    /// <summary>Attributes compared: read-only, hidden, system, folder, reparse point. Archive and indexing flags change by
    /// themselves and say nothing.</summary>
    public const int AttributeMask = 0x1 | 0x2 | 0x4 | 0x10 | 0x400;

    /// <summary>Folders whose content is never kept (only the empty folder): temporary files and caches of browsers and
    /// Windows. Paths from the profile folder.</summary>
    public static readonly IReadOnlyList<string> SkippedFolders =
    [
        @"AppData\Local\Temp",
        @"AppData\Local\CrashDumps",
        @"AppData\Local\Microsoft\Windows\INetCache",
        @"AppData\Local\Microsoft\Windows\WER",
    ];

    /// <summary>Folder names that are caches wherever they are under AppData\Local (browsers and Electron programs).</summary>
    public static readonly IReadOnlyList<string> CacheNames = ["Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "DawnCache"];

    /// <summary>The content of this folder is not kept.</summary>
    public static bool SkipContent(string folder)
    {
        if (SkippedFolders.Any(s => string.Equals(s, folder, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var name = folder[(folder.LastIndexOf('\\') + 1)..];
        return folder.StartsWith(@"AppData\Local\", StringComparison.OrdinalIgnoreCase)
            && CacheNames.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A path inside a profile: relative, no "." or "..", no drive, no stream, no empty part.</summary>
    public static bool SafePath(string path) =>
        path.Length is > 0 and < 32000
        && !path.StartsWith('\\')
        && path.IndexOfAny([':', '/', '\0', '*', '?', '"', '<', '>', '|']) < 0
        && path.Split('\\').All(part => part.Length > 0 && part != "." && part != "..");

    public static bool Same(ProfileItem now, ProfileItem kept) =>
        now.Kind == kept.Kind
        && (now.Attributes & AttributeMask) == (kept.Attributes & AttributeMask)
        && (now.Kind != VaultKind.File || (now.Size == kept.Size && now.WriteTime == kept.WriteTime));

    /// <summary>What to do so that the profile is as the trezor: what is not in it goes, what differs or is missing comes
    /// back. A thing of another kind goes and comes back.</summary>
    public static VaultPlan Plan(IEnumerable<ProfileItem> now, VaultManifest manifest)
    {
        var kept = new Dictionary<string, VaultEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in manifest.Entries)
        {
            kept[e.Path] = e;
        }

        var remove = new List<ProfileItem>();
        var present = new Dictionary<string, ProfileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in now)
        {
            present[item.Path] = item;
            if (!kept.TryGetValue(item.Path, out var entry) || entry.Kind != item.Kind)
            {
                remove.Add(item);
            }
        }

        // what lies inside a folder that goes away goes with it – only the folder itself is listed
        var gone = new HashSet<string>(remove.Where(r => r.Kind == VaultKind.Directory).Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        remove = [.. remove.Where(r => !Inside(r.Path, gone)).OrderByDescending(r => r.Path.Count(c => c == '\\'))];

        var put = manifest.Entries
            .Where(e => !present.TryGetValue(e.Path, out var item) || item.Kind != e.Kind || !Same(item, e.Item))
            .ToList();
        return new VaultPlan(remove, put);
    }

    private static bool Inside(string path, HashSet<string> folders)
    {
        for (var cut = path.LastIndexOf('\\'); cut > 0; cut = path.LastIndexOf('\\', cut - 1))
        {
            if (folders.Contains(path[..cut]))
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<string> Problems(VaultManifest m)
    {
        if (m.Schema != VaultManifest.CurrentSchema)
        {
            yield return $"neznáma schéma trezoru {m.Schema}";
        }

        if (m.Account.Length == 0 || !m.Sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
        {
            yield return "trezor nemá konto a jeho SID";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in m.Entries)
        {
            if (!SafePath(e.Path))
            {
                yield return $"nebezpečná cesta v trezore: {e.Path}";
            }
            else if (!seen.Add(e.Path))
            {
                yield return $"cesta dvakrát: {e.Path}";
            }
            else if (e.Path.LastIndexOf('\\') is var cut && cut > 0 && !folders.Contains(e.Path[..cut]))
            {
                yield return $"{e.Path}: jeho priečinok nie je pred ním";
            }

            if (e.Kind == VaultKind.Directory)
            {
                folders.Add(e.Path);
            }

            if (e.Offset < 0 || e.Length < 0 || e.Offset + e.Length > m.ArchiveLength || e.Sha256.Length != 64)
            {
                yield return $"{e.Path}: miesto v archíve nesedí";
            }
        }
    }

    public static string ToJson(VaultManifest m) => JsonText.Canonical(new JsonObject()
        .Add("schema", m.Schema)
        .Add("account", m.Account)
        .Add("sid", m.Sid)
        .Add("profileCreated", m.ProfileCreated)
        .Add("windows", m.Windows)
        .Add("savedUtc", m.SavedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        .Add("archiveLength", m.ArchiveLength)
        .Add("entries", JsonArray.Of(m.Entries, e => new JsonObject()
            .Add("path", e.Path)
            .AddIf(e.Kind != VaultKind.File, "kind", e.Kind.ToString())
            .AddIf(e.Size != 0, "size", e.Size)
            .Add("time", e.WriteTime)
            .Add("attr", e.Attributes)
            .Add("at", e.Offset)
            .Add("len", e.Length)
            .Add("sha", e.Sha256))));

    public static VaultManifest FromJson(string json)
    {
        var f = new JsonFields(JsonReader.Parse(json, new JsonSyntax(false, false, 8, 256 * 1024 * 1024)), "trezor");
        var m = new VaultManifest
        {
            Schema = f.Int("schema", 0),
            Account = f.String("account"),
            Sid = f.String("sid"),
            ProfileCreated = f.Long("profileCreated", 0),
            Windows = f.String("windows"),
            SavedUtc = f.Time("savedUtc"),
            ArchiveLength = f.Long("archiveLength", 0, 0),
        };
        foreach (var node in f.Array("entries"))
        {
            var e = new JsonFields(node, "položka trezoru");
            m.Entries.Add(new VaultEntry(
                e.String("path"),
                e.Enum("kind", VaultKind.File),
                e.Long("size", 0, 0),
                e.Long("time", 0),
                e.Int("attr", 0),
                e.Long("at", 0, 0),
                e.Long("len", 0, 0),
                e.String("sha")));
            e.End();
        }

        f.End();
        return m;
    }
}
