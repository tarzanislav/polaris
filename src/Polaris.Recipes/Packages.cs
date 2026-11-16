using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Polaris.Json;

namespace Polaris.Recipes;

public enum PackageKind
{
    /// <summary>A Windows Installer package: installed, recognised and removed by Windows Installer itself.</summary>
    Msi,

    /// <summary>An installer program: silent arguments given, recognised by its name in the installed programs.</summary>
    Exe,
}

/// <summary>A program element's link to the School supply (Zásoba): the package and the fingerprint of its description.
/// The description holds the fingerprint of the installer, so a signed recipe pins every byte that will run.</summary>
public sealed class PackageRef
{
    public string Id { get; set; } = "";

    /// <summary>SHA-256 of the canonical form of the package's description (balik.json).</summary>
    public string Fingerprint { get; set; } = "";
}

/// <summary>One installer in the supply with everything needed to install it without asking anybody: how, how to know it
/// is there, how to remove it, and who published it.</summary>
public sealed class Package
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Version { get; set; } = "";

    public PackageKind Kind { get; set; }

    /// <summary>The installer's file name, inside the package's folder.</summary>
    public string File { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public long Size { get; set; }

    /// <summary>MSI: properties added to the silent installation; EXE: the silent arguments (required).</summary>
    public string Arguments { get; set; } = "";

    /// <summary>EXE: arguments for the uninstaller when Windows knows no silent way.</summary>
    public string? UninstallArguments { get; set; }

    /// <summary>The signer of the installer (Authenticode) when it was added; the agent then demands the same signer.</summary>
    public string? Publisher { get; set; }

    /// <summary>MSI: the product code Windows Installer recognises the product by.</summary>
    public string? ProductCode { get; set; }

    public string? UpgradeCode { get; set; }

    /// <summary>EXE: the name in the list of installed programs (MSI: for people only).</summary>
    public string DisplayName { get; set; } = "";

    public NameMatch Match { get; set; } = NameMatch.Exact;

    public DateTimeOffset AddedUtc { get; set; }

    public string Note { get; set; } = "";
}

/// <summary>The description of a package as JSON (balik.json), checks and the fingerprint.</summary>
public static partial class PackageFormat
{
    public const string FileName = "balik.json";

    public static string ToJson(Package p) => JsonText.Readable(Write(p));

    public static string Fingerprint(Package p) => JsonText.Fingerprint(Write(p));

    public static JsonObject Write(Package p) => new JsonObject()
        .Add("schema", p.Schema)
        .Add("id", p.Id)
        .Add("title", p.Title)
        .AddIf(p.Version.Length > 0, "version", p.Version)
        .Add("kind", p.Kind.ToString())
        .Add("file", p.File)
        .Add("sha256", p.Sha256)
        .Add("size", p.Size)
        .AddIf(p.Arguments.Length > 0, "arguments", p.Arguments)
        .AddIf("uninstallArguments", p.UninstallArguments is { } u ? (JsonNode)u : null)
        .AddIf("publisher", p.Publisher is { Length: > 0 } s ? (JsonNode)s : null)
        .AddIf("productCode", p.ProductCode is { Length: > 0 } c ? (JsonNode)c : null)
        .AddIf("upgradeCode", p.UpgradeCode is { Length: > 0 } g ? (JsonNode)g : null)
        .AddIf(p.DisplayName.Length > 0, "displayName", p.DisplayName)
        .AddIf(p.Match != NameMatch.Exact, "match", p.Match.ToString())
        .Add("addedUtc", p.AddedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        .AddIf(p.Note.Length > 0, "note", p.Note);

    public static Package FromJson(string json, string what = "balík")
    {
        try
        {
            var f = new JsonFields(JsonReader.Parse(json, JsonSyntax.Human), what);
            var schema = f.Int("schema", Package.CurrentSchema);
            if (schema != Package.CurrentSchema)
            {
                throw new InvalidDataException($"Balík má schému {schema}, tento program pozná {Package.CurrentSchema}.");
            }

            var p = new Package
            {
                Id = f.String("id"),
                Title = f.String("title"),
                Version = f.String("version"),
                Kind = f.Enum("kind", PackageKind.Exe),
                File = f.String("file"),
                Sha256 = f.String("sha256"),
                Size = f.Long("size", 0, 0, long.MaxValue),
                Arguments = f.String("arguments"),
                UninstallArguments = f.OptionalString("uninstallArguments"),
                Publisher = f.OptionalString("publisher"),
                ProductCode = f.OptionalString("productCode"),
                UpgradeCode = f.OptionalString("upgradeCode"),
                DisplayName = f.String("displayName"),
                Match = f.Enum("match", NameMatch.Exact),
                AddedUtc = f.Time("addedUtc"),
                Note = f.String("note"),
            };
            f.End();
            return p;
        }
        catch (JsonError e)
        {
            throw new InvalidDataException($"Popis balíka sa nedá prečítať: {e.Message}", e);
        }
    }

    /// <summary>What is wrong with a description, in Slovak; empty = fine.</summary>
    public static List<string> Problems(Package p)
    {
        var list = new List<string>();
        if (!IdPattern().IsMatch(p.Id))
        {
            list.Add("id balíka: malé písmená bez diakritiky, číslice, '-', '.', '_', najviac 64 znakov (napr. audacity-3.7.4).");
        }

        if (string.IsNullOrWhiteSpace(p.Title))
        {
            list.Add("chýba názov balíka.");
        }

        if (p.File.Length == 0 || p.File.IndexOfAny(['/', '\\', ':']) >= 0 || p.File is "." or ".." || p.File.Equals(FileName, StringComparison.OrdinalIgnoreCase))
        {
            list.Add("file musí byť meno súboru inštalátora v priečinku balíka (bez cesty).");
        }

        if (!Sha256Pattern().IsMatch(p.Sha256))
        {
            list.Add("sha256 musí byť odtlačok SHA-256 (64 znakov 0-9 a-f).");
        }

        if (p.Size <= 0)
        {
            list.Add("veľkosť inštalátora chýba.");
        }

        var extension = Path.GetExtension(p.File).ToLowerInvariant();
        if (p.Kind == PackageKind.Msi)
        {
            if (extension != ".msi")
            {
                list.Add("balík MSI musí mať súbor .msi.");
            }

            if (p.ProductCode is not { } code || !GuidPattern().IsMatch(code))
            {
                list.Add("balík MSI potrebuje productCode v tvare {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}.");
            }
        }
        else
        {
            if (extension != ".exe")
            {
                list.Add("inštalátor typu Exe musí mať súbor .exe.");
            }

            if (p.Arguments.Trim().Length == 0)
            {
                list.Add("inštalátor .exe potrebuje argumenty tichej inštalácie (napr. /S alebo /quiet) – inak by čakal na kliknutie.");
            }

            if (p.DisplayName.Trim().Length < 3)
            {
                list.Add("inštalátor .exe potrebuje displayName (aspoň 3 znaky) – podľa neho agent pozná, že program je nainštalovaný.");
            }
        }

        if (p.Arguments.Contains('\n', StringComparison.Ordinal) || (p.UninstallArguments?.Contains('\n', StringComparison.Ordinal) ?? false))
        {
            list.Add("argumenty nesmú mať viac riadkov.");
        }

        return list;
    }

    /// <summary>A short id from a title and version: "Audacity 3.7.4" → "audacity-3.7.4".</summary>
    public static string SuggestId(string title, string version)
    {
        var text = (title + (version.Length > 0 && !title.Contains(version, StringComparison.Ordinal) ? " " + version : "")).ToLowerInvariant();
        var id = new StringBuilder();
        foreach (var c in text)
        {
            var plain = Plain(c);
            if (char.IsAsciiLetterOrDigit(plain) || plain is '.' or '_')
            {
                id.Append(plain);
            }
            else if (id.Length > 0 && id[^1] != '-')
            {
                id.Append('-');
            }
        }

        var result = id.ToString().Trim('-', '.');
        return result.Length > 64 ? result[..64].TrimEnd('-', '.') : result;
    }

    private static char Plain(char c) => c switch
    {
        'á' or 'ä' or 'à' or 'â' => 'a',
        'č' or 'ç' => 'c',
        'ď' => 'd',
        'é' or 'ě' or 'è' or 'ë' => 'e',
        'í' or 'î' => 'i',
        'ĺ' or 'ľ' => 'l',
        'ň' => 'n',
        'ó' or 'ô' or 'ö' => 'o',
        'ŕ' or 'ř' => 'r',
        'š' => 's',
        'ť' => 't',
        'ú' or 'ů' or 'ü' => 'u',
        'ý' => 'y',
        'ž' => 'z',
        _ => c,
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    internal static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^\\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\\}$")]
    private static partial Regex GuidPattern();
}

/// <summary>The School supply: a folder (on the PC, or a share of the school's server read-only for PCs) with one folder
/// per package – its description balik.json and the installer. No index to keep in step: the folders are the list.</summary>
public sealed class PackageStore(string root)
{
    public string Root { get; } = root;

    public string FolderOf(string id) => Path.Combine(Root, "baliky", id);

    public bool Exists(string id) => File.Exists(Path.Combine(FolderOf(id), PackageFormat.FileName));

    /// <summary>The description of a package; when <paramref name="fingerprint"/> is given it must be exactly that one –
    /// a description changed after the recipe was signed is refused.</summary>
    public Package Load(string id, string? fingerprint = null)
    {
        if (!PackageFormat.IdPattern().IsMatch(id))
        {
            throw new InvalidDataException($"Neplatné id balíka „{id}“.");
        }

        var file = Path.Combine(FolderOf(id), PackageFormat.FileName);
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"Balík {id} v zásobe {Root} nie je.");
        }

        var p = PackageFormat.FromJson(File.ReadAllText(file, Encoding.UTF8), $"balík {id}");
        if (p.Id != id)
        {
            throw new InvalidDataException($"Popis v priečinku {id} patrí balíku {p.Id}.");
        }

        if (PackageFormat.Problems(p) is { Count: > 0 } problems)
        {
            throw new InvalidDataException($"Balík {id} má problémy: {problems[0]}");
        }

        if (fingerprint is not null && !string.Equals(PackageFormat.Fingerprint(p), fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Popis balíka {id} sa zmenil po schválení receptu (iný odtlačok) – balík nepoužijem.");
        }

        return p;
    }

    public string InstallerOf(Package p) => Path.Combine(FolderOf(p.Id), p.File);

    public IEnumerable<string> Ids()
    {
        var folder = Path.Combine(Root, "baliky");
        return Directory.Exists(folder)
            ? Directory.EnumerateDirectories(folder).Select(Path.GetFileName).OfType<string>().Where(n => File.Exists(Path.Combine(folder, n, PackageFormat.FileName))).Order(StringComparer.Ordinal)
            : [];
    }

    /// <summary>Copies an installer into the supply and writes its description; an existing package is never overwritten.</summary>
    public Package Add(string installer, Package p)
    {
        p.File = Path.GetFileName(installer);
        p.Size = new FileInfo(installer).Length;
        p.Sha256 = RecipeText.FileFingerprint(installer);
        if (PackageFormat.Problems(p) is { Count: > 0 } problems)
        {
            throw new InvalidDataException(problems[0]);
        }

        var folder = FolderOf(p.Id);
        if (Directory.Exists(folder))
        {
            throw new InvalidDataException($"Balík {p.Id} v zásobe už je – nový musí mať iné id (napr. s verziou).");
        }

        var temporary = folder + ".novy-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(temporary);
        try
        {
            File.Copy(installer, Path.Combine(temporary, p.File));
            if (RecipeText.FileFingerprint(Path.Combine(temporary, p.File)) != p.Sha256)
            {
                throw new IOException("Kópia inštalátora sa líši od originálu – zápis do zásoby zlyhal.");
            }

            File.WriteAllText(Path.Combine(temporary, PackageFormat.FileName), PackageFormat.ToJson(p), new UTF8Encoding(false));
            Directory.Move(temporary, folder); // the package appears whole or not at all
        }
        catch
        {
            Directory.Delete(temporary, recursive: true);
            throw;
        }

        return p;
    }

    /// <summary>Checks every package: description, size and fingerprint of the installer. Problems in Slovak.</summary>
    public List<string> Verify()
    {
        var problems = new List<string>();
        foreach (var id in Ids())
        {
            try
            {
                var p = Load(id);
                var file = InstallerOf(p);
                if (!File.Exists(file))
                {
                    problems.Add($"{id}: chýba inštalátor {p.File}.");
                }
                else if (new FileInfo(file).Length != p.Size || RecipeText.FileFingerprint(file) != p.Sha256)
                {
                    problems.Add($"{id}: inštalátor {p.File} sa zmenil (iný odtlačok) – nepoužije sa.");
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                problems.Add($"{id}: {e.Message}");
            }
        }

        return problems;
    }

    /// <summary>The snippet for a recipe that pins this package.</summary>
    public static string RecipeSnippet(Package p) =>
        JsonText.Canonical(new JsonObject().Add("id", p.Id).Add("fingerprint", PackageFormat.Fingerprint(p)));
}
