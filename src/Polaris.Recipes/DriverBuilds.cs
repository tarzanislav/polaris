using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Polaris.Recipes;

/// <summary>What the firmware of a PC says about it (SMBIOS, as Windows keeps it under HARDWARE\DESCRIPTION\System\BIOS):
/// the maker and model of the system and of the board.</summary>
public sealed record HardwareInfo(
    string SystemManufacturer,
    string SystemProduct,
    string SystemVersion = "",
    string BoardManufacturer = "",
    string BoardProduct = "");

/// <summary>A backup of drivers found in the folder of backups: where it is and its description.</summary>
public sealed record FoundBackup(string Folder, DriverBackup Backup);

/// <summary>The hardware build of a PC by name – "Maker Model - Windows 11 Pro" – for the folder of its backup of drivers
/// and for finding the backup that belongs to a PC of the same build. Backups never stay on the PC's own disks: they go
/// to the folder of backups (later the console's disk, now a USB stick or a shared folder), one folder per build.</summary>
public static partial class DriverBuilds
{
    /// <summary>The folder of backups when the administrator set none: on the first USB stick (removable disk).</summary>
    public const string DefaultFolderOnStick = "POLARIS-ovladace";

    /// <summary>Texts that firmware of boards from the shop leaves where the maker should write the model.</summary>
    public static readonly IReadOnlyList<string> Fillers =
    [
        "To be filled by O.E.M.", "To Be Filled By O.E.M", "System manufacturer", "System Product Name", "System Version",
        "Default string", "Not Applicable", "Not Specified", "None", "OEM", "O.E.M.", "Type1ProductConfigId", "All Series",
        "Undefined", "Unknown", "Invalid", "x.x", "0", "1", "123456789", "Base Board Product Name", "Base Board Manufacturer",
    ];

    /// <summary>Makers by the short name people use (firmware writes the company: "Dell Inc.", "ASUSTeK COMPUTER INC.").</summary>
    private static readonly (string Starts, string Name)[] Makers =
    [
        ("Hewlett-Packard", "HP"), ("HP", "HP"), ("Dell", "Dell"), ("LENOVO", "Lenovo"), ("ASUSTeK", "ASUS"), ("ASUS", "ASUS"),
        ("Micro-Star", "MSI"), ("MSI", "MSI"), ("Gigabyte", "Gigabyte"), ("Acer", "Acer"), ("FUJITSU", "Fujitsu"),
        ("TOSHIBA", "Toshiba"), ("Dynabook", "Dynabook"), ("Intel", "Intel"), ("ASRock", "ASRock"), ("Microsoft", "Microsoft"),
        ("Apple", "Apple"), ("Samsung", "Samsung"), ("Medion", "Medion"), ("Panasonic", "Panasonic"), ("Getac", "Getac"),
    ];

    public static bool Filler(string text) =>
        text.Trim().Length == 0 || Fillers.Any(f => string.Equals(f.TrimEnd('.'), text.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase));

    /// <summary>The maker as people say it: the known short name, else the company without "Inc.", "Co., Ltd." …</summary>
    public static string Maker(string manufacturer)
    {
        var m = Spaces(manufacturer);
        foreach (var (starts, name) in Makers)
        {
            if (m.StartsWith(starts, StringComparison.OrdinalIgnoreCase) && (m.Length == starts.Length || !char.IsLetter(m[starts.Length])))
            {
                return name;
            }
        }

        string before;
        do
        {
            before = m;
            m = CompanySuffix().Replace(m, "").Trim().TrimEnd(',').Trim();
        }
        while (m != before && m.Length > 0);

        return m.Length == 0 ? Spaces(manufacturer) : m;
    }

    /// <summary>"Maker Model" of the PC: the system's maker and model; Lenovo keeps the model in the version (the product
    /// is the machine type); a PC built from parts (fillers instead of the model) is named by its board. Null when
    /// nothing tells the model.</summary>
    public static string? Model(HardwareInfo hw)
    {
        var maker = Filler(hw.SystemManufacturer) ? "" : Maker(hw.SystemManufacturer);
        var model = Filler(hw.SystemProduct) ? "" : Spaces(hw.SystemProduct);
        if (maker == "Lenovo" && !Filler(hw.SystemVersion) && hw.SystemVersion.Trim().Contains(' ', StringComparison.Ordinal))
        {
            model = Spaces(hw.SystemVersion);
        }

        if (model.Length == 0)
        {
            if (Filler(hw.BoardProduct))
            {
                return null;
            }

            maker = Filler(hw.BoardManufacturer) ? maker : Maker(hw.BoardManufacturer);
            model = Spaces(hw.BoardProduct);
        }

        // "HP EliteDesk …" from the maker "Hewlett-Packard": the maker is not said twice
        if (maker.Length > 0 && model.StartsWith(maker + " ", StringComparison.OrdinalIgnoreCase))
        {
            maker = "";
        }

        return (maker.Length > 0 ? maker + " " + model : model).Trim();
    }

    /// <summary>The edition of Windows as people say it: Windows 11 keeps "Windows 10" in its product name; the build
    /// tells (from 22000 it is Windows 11).</summary>
    public static string Edition(string productName, int build)
    {
        var name = Spaces(productName);
        if (name.Length == 0)
        {
            return build >= 22000 ? "Windows 11" : "Windows";
        }

        if (build >= 22000 && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            name = "Windows 11" + name["Windows 10".Length..];
        }

        return name;
    }

    /// <summary>The name of the build, e.g. "Maker Model - Windows 11 Pro".</summary>
    public static string Name(string? model, string edition) =>
        (model is { Length: > 0 } ? model + " - " : "Neznámy model - ") + edition;

    /// <summary>A name as a folder: no characters Windows forbids in names, no dots or spaces at the end, at most 100 chars.</summary>
    public static string FolderName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            sb.Append(c < 32 || "\\/:*?\"<>|".Contains(c, StringComparison.Ordinal) ? ' ' : c);
        }

        var text = Spaces(sb.ToString());
        if (text.Length > 100)
        {
            text = text[..100];
        }

        text = text.TrimEnd('.', ' ');
        return text.Length == 0 ? "Zostava" : text;
    }

    /// <summary>The folder for a new backup of the build under the folder of backups: the name of the build, with the day
    /// when such a folder is already there (and a number when even that one is) – an older backup is never overwritten.</summary>
    public static string NewFolder(string root, string name, DateTime today, Func<string, bool> taken)
    {
        var first = Path.Combine(root, FolderName(name));
        if (!taken(first))
        {
            return first;
        }

        var day = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dated = $"{first} ({day})";
        for (var n = 2; taken(dated); n++)
        {
            dated = $"{first} ({day}, {n})";
        }

        return dated;
    }

    /// <summary>The backup for a PC of the build: the same model and Windows first, else the same model (a backup from the
    /// other Windows – drivers of Windows 10 mostly serve Windows 11 too); the newest of them. Backups from before the
    /// model was recorded are matched by the name of their folder.</summary>
    public static FoundBackup? Choose(IEnumerable<FoundBackup> found, string? model, string edition)
    {
        if (model is not { Length: > 0 })
        {
            return null;
        }

        var same = found.Where(f => SameModel(f, model)).ToList();
        var exact = same.Where(f => string.Equals(EditionOf(f), edition, StringComparison.OrdinalIgnoreCase)).ToList();
        return (exact.Count > 0 ? exact : same).OrderByDescending(f => f.Backup.CreatedUtc).FirstOrDefault();
    }

    public static bool SameModel(FoundBackup f, string model) =>
        f.Backup.Hardware.Length > 0
            ? string.Equals(f.Backup.Hardware, model, StringComparison.OrdinalIgnoreCase)
            : Path.GetFileName(f.Folder.TrimEnd('\\', '/')).StartsWith(FolderName(model) + " - ", StringComparison.OrdinalIgnoreCase);

    private static string EditionOf(FoundBackup f)
    {
        if (f.Backup.Edition.Length > 0)
        {
            return f.Backup.Edition;
        }

        var folder = Path.GetFileName(f.Folder.TrimEnd('\\', '/'));
        var at = folder.LastIndexOf(" - ", StringComparison.Ordinal);
        return at < 0 ? "" : DatedSuffix().Replace(folder[(at + 3)..], "");
    }

    private static string Spaces(string text) => MoreSpaces().Replace(text.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex MoreSpaces();

    [GeneratedRegex(@"[\s,]*\b(Inc|Corp|Corporation|Co|Ltd|Limited|LLC|GmbH|AG|S\.?A|B\.?V|Technology|Computer|International|Electronics|s\.?r\.?o|a\.?s)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffix();

    [GeneratedRegex(@"\s*\(\d{4}-\d{2}-\d{2}(, \d+)?\)$")]
    private static partial Regex DatedSuffix();
}
