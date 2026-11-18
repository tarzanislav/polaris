using System.Text.RegularExpressions;

namespace Polaris.Recipes;

/// <summary>Rules of software in a lab: only stable versions – never a beta, nightly, preview or release candidate.</summary>
public static partial class SoftwareRules
{
    /// <summary>Parts of a WinGet id that mark a test channel (Mozilla.Firefox.Nightly, NanaZip.Preview …).</summary>
    public static readonly IReadOnlyList<string> TestChannels =
        ["Beta", "Nightly", "Dev", "Developer", "DeveloperEdition", "Canary", "Preview", "Insider", "Insiders", "Alpha", "RC", "Daily", "Snapshot", "EarlyAccess", "Unstable", "Testing"];

    /// <summary>The part of the id that marks a test channel, or null for a stable package.</summary>
    public static string? TestChannelOf(string wingetId) =>
        wingetId.Split('.').Skip(1).FirstOrDefault(part => TestChannels.Contains(part, StringComparer.OrdinalIgnoreCase));

    /// <summary>A version like 3.0-beta2, 1.2rc1, 5.0.0-preview.3, 2.1a.</summary>
    public static bool TestVersion(string version) => TestVersionPattern().IsMatch(version);

    [GeneratedRegex(@"(alpha|beta|preview|pre|rc|dev|nightly|canary|snapshot|insider)\d*|\d[ab]\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex TestVersionPattern();
}

public enum FolderRights
{
    Read,

    /// <summary>Read, write, create and delete inside – not change the rights or take the folder.</summary>
    Modify,
}

/// <summary>Who gets a right on a folder: the users of the PC, every signed-in user, or one local account.</summary>
public sealed class FolderAccess
{
    /// <summary>"Users" (all users of the PC), "AuthenticatedUsers" or the name of a local account (e.g. ziak).</summary>
    public string Who { get; set; } = "Users";

    public FolderRights Rights { get; set; } = FolderRights.Modify;
}

/// <summary>A folder of the PC and the rights on it – e.g. C:\Data\Ucivo writable for pupils. The rights are added to
/// what the folder has (inherited by everything inside); nothing else is taken away. A folder is never deleted: absent
/// (present false) = the rights the recipe gave are taken away, the folder and its content stay. Never in system
/// folders, profiles or the programs' folders (a pupil could change a program an administrator later runs).</summary>
public sealed class FolderElement : Element
{
    public string Path { get; set; } = "";

    public List<FolderAccess> Access { get; set; } = [];

    public override string Key => "folder:" + Path.Trim().TrimEnd('\\').ToLowerInvariant();

    public override string Title => $"priečinok {Path}";
}

public static partial class FolderRules
{
    public const string UsersSid = "S-1-5-32-545";
    public const string AuthenticatedUsersSid = "S-1-5-11";

    /// <summary>Folders (and what is under them) a recipe never touches.</summary>
    public static readonly IReadOnlyList<string> Forbidden =
    [
        @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData\Microsoft", @"C:\ProgramData\POLARIS",
        @"C:\Recovery", @"C:\System Volume Information", @"C:\$Recycle.Bin", @"C:\Boot", @"C:\EFI",
    ];

    /// <summary>Groups of Windows that are not given rights by a recipe (everybody, administrators, guests).</summary>
    public static readonly IReadOnlyList<string> Groups = ["Everyone", "Všetci", "Administrators", "Správcovia", "Guests", "Hostia"];

    public static IEnumerable<string> Problems(FolderElement f)
    {
        var path = f.Path.Trim().TrimEnd('\\');
        if (!PathPattern().IsMatch(path) || path.Split('\\').Skip(1).Any(p => p is "." or ".." || p.Trim().Length == 0))
        {
            yield return "path musí byť úplná cesta k priečinku na disku PC, napr. C:\\Data\\Ucivo.";
            yield break;
        }

        if (path.Length <= 3)
        {
            yield return "koreň disku recept nemení.";
        }

        if (Forbidden.Any(x => Under(path, x)))
        {
            yield return $"„{path}“ je systémový priečinok alebo priečinok programov – recept ho nemení (žiak by mohol zmeniť program, ktorý neskôr spustí správca).";
        }

        if (Under(path, @"C:\Users") && !Under(path, @"C:\Users\Public"))
        {
            yield return "v C:\\Users len pod C:\\Users\\Public – profily kont spravuje Windows a trezor.";
        }

        if (f.Access.Count == 0)
        {
            yield return "access: komu aké práva (napr. { \"who\": \"Users\", \"rights\": \"Modify\" }).";
        }

        foreach (var a in f.Access)
        {
            var who = a.Who.Trim();
            if (who is not ("Users" or "AuthenticatedUsers") && !AccountPattern().IsMatch(who))
            {
                yield return $"who „{a.Who}“: Users, AuthenticatedUsers alebo meno lokálneho konta.";
            }
            else if (SessionPolicy.NeverClean.Contains(who) || Groups.Contains(who, StringComparer.OrdinalIgnoreCase))
            {
                yield return $"konto „{who}“ je vstavané konto Windows.";
            }
        }
    }

    public static bool Under(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z]:\\[^/:*?""<>|\x00-\x1f]+$")]
    private static partial Regex PathPattern();

    [GeneratedRegex("^[^\"/\\\\\\[\\]:;|=,+*?<>@]{1,20}$")]
    private static partial Regex AccountPattern();
}
