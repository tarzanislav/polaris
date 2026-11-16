namespace Polaris.Recipes;

/// <summary>One thing a PC should have (or must not have): a program, a registry value, bookmarks … Every kind has its own
/// small executor in the agent that can read the real state, compare it and change it.</summary>
public abstract class Element
{
    /// <summary>Unique name of the element within a recipe (small letters, digits, '-', '.', '_').</summary>
    public string Id { get; set; } = "";

    /// <summary>True = the thing must be there; false = it must not be there (it is removed).</summary>
    public bool Present { get; set; } = true;

    /// <summary>Why the element is in the recipe – for the admin, not used by the agent.</summary>
    public string? Note { get; set; }

    /// <summary>What the element manages. Two elements with the same key manage the same thing; when they come from
    /// several assignments, the narrower scope wins.</summary>
    public abstract string Key { get; }

    /// <summary>A short Slovak description for plans and reports.</summary>
    public abstract string Title { get; }
}

public enum NameMatch
{
    /// <summary>The name in the list of installed programs is exactly this.</summary>
    Exact,

    /// <summary>The name starts with this (for names that carry the version, e.g. "7-Zip 25.01 (x64)").</summary>
    Prefix,
}

/// <summary>An installer file kept in the store next to the recipe, checked by its fingerprint before it runs.</summary>
public sealed class InstallerSource
{
    /// <summary>Path relative to the folder of the recipe (e.g. "subory/program.msi").</summary>
    public string File { get; set; } = "";

    /// <summary>SHA-256 of the file; a file with another fingerprint is never run.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Arguments for a silent installation (an .msi gets "/qn /norestart" when this is empty).</summary>
    public string Arguments { get; set; } = "";

    /// <summary>Arguments for the uninstaller when Windows knows no silent way (e.g. "/S").</summary>
    public string? UninstallArguments { get; set; }
}

/// <summary>A program for the whole PC. It is recognised by its name in the list of installed programs, installed from
/// WinGet or from an installer in the store.</summary>
public sealed class ProgramElement : Element
{
    /// <summary>Package of WinGet (e.g. "VideoLAN.VLC").</summary>
    public string? Winget { get; set; }

    /// <summary>The version WinGet installs; empty = the newest and any installed version is fine. With a version, a
    /// program with another version is upgraded (or reinstalled when WinGet cannot go there).</summary>
    public string? Version { get; set; }

    /// <summary>Or an installer file from the store.</summary>
    public InstallerSource? Installer { get; set; }

    /// <summary>How Windows names the program in the installed programs – this is how it is recognised.</summary>
    public string DisplayName { get; set; } = "";

    public NameMatch Match { get; set; } = NameMatch.Exact;

    public override string Key => "program:" + DisplayName.Trim().ToLowerInvariant();

    public override string Title => Winget is { Length: > 0 } ? $"program {DisplayName} (WinGet {Winget})" : $"program {DisplayName}";
}

public enum RegistryKind
{
    String,
    ExpandString,
    DWord,
    QWord,
    MultiString,
}

/// <summary>One value in the machine part of the registry (HKLM).</summary>
public sealed class RegistryValueElement : Element
{
    /// <summary>Full path of the key, e.g. HKLM\SOFTWARE\Policies\Microsoft\Edge.</summary>
    public string Path { get; set; } = "";

    /// <summary>Name of the value ("" = the default value of the key).</summary>
    public string Name { get; set; } = "";

    public RegistryKind Kind { get; set; } = RegistryKind.String;

    /// <summary>The data as text: numbers decimal or with 0x, more lines of a multi-string separated by a new line.</summary>
    public string Data { get; set; } = "";

    public override string Key => "registry:" + RegistryPath.Normalize(Path).ToLowerInvariant() + "|" + Name.ToLowerInvariant();

    public override string Title => $"hodnota registra {RegistryPath.Normalize(Path)}\\{(Name.Length == 0 ? "(predvolená)" : Name)}";
}

public enum Browser
{
    Edge,
    Chrome,
}

/// <summary>A bookmark or a folder of bookmarks.</summary>
public sealed class Bookmark
{
    public string Name { get; set; } = "";

    /// <summary>The address; a folder has none.</summary>
    public string? Url { get; set; }

    /// <summary>Bookmarks inside a folder.</summary>
    public List<Bookmark>? Children { get; set; }
}

/// <summary>Bookmarks every user of the PC sees in the browser (managed bookmarks – the pupil cannot delete them, his own
/// bookmarks stay possible).</summary>
public sealed class BookmarksElement : Element
{
    public List<Browser> Browsers { get; set; } = [Browser.Edge, Browser.Chrome];

    /// <summary>Name of the folder the bookmarks appear in (e.g. "Škola").</summary>
    public string Folder { get; set; } = "";

    /// <summary>Show the bar with bookmarks under the address line.</summary>
    public bool ShowBar { get; set; } = true;

    public List<Bookmark> Items { get; set; } = [];

    public override string Key => "bookmarks:" + string.Join(",", Browsers.Distinct().Order().Select(b => b.ToString().ToLowerInvariant()));

    public override string Title => $"záložky „{Folder}“ ({string.Join(", ", Browsers.Distinct().Order())})";
}

public enum ServiceStart
{
    Automatic,

    /// <summary>Automatic, but a little after the start of Windows.</summary>
    AutomaticDelayed,
    Manual,
    Disabled,
}

/// <summary>How a Windows service starts and whether it runs. A service cannot be removed by a recipe – it is disabled.</summary>
public sealed class ServiceElement : Element
{
    /// <summary>Name of the service (its key name, e.g. "Spooler"), not the name shown in the list.</summary>
    public string Name { get; set; } = "";

    public ServiceStart Start { get; set; } = ServiceStart.Manual;

    /// <summary>true = must run, false = must be stopped, empty = no matter.</summary>
    public bool? Running { get; set; }

    public override string Key => "service:" + Name.Trim().ToLowerInvariant();

    public override string Title => $"služba {Name}";
}

/// <summary>An optional part of Windows switched on (present) or off (absent), e.g. Windows Sandbox
/// ("Containers-DisposableClientVM").</summary>
public sealed class FeatureElement : Element
{
    /// <summary>Name of the feature as Windows knows it.</summary>
    public string Name { get; set; } = "";

    public override string Key => "feature:" + Name.Trim().ToLowerInvariant();

    public override string Title => $"súčasť Windows {Name}";
}

/// <summary>A value every new profile gets: it is written into the template of profiles (the profile "Default"), from
/// which Windows makes the profile of every user at the first sign-in – and of the pupil after every cleaning.</summary>
public sealed class UserRegistryElement : Element
{
    /// <summary>Path in the user part of the registry, without HKCU (e.g. Control Panel\Desktop).</summary>
    public string Path { get; set; } = "";

    public string Name { get; set; } = "";

    public RegistryKind Kind { get; set; } = RegistryKind.String;

    public string Data { get; set; } = "";

    public override string Key => "user-registry:" + UserRegistryPath.Normalize(Path).ToLowerInvariant() + "|" + Name.ToLowerInvariant();

    public override string Title => $"predloha profilu {UserRegistryPath.Normalize(Path)}\\{(Name.Length == 0 ? "(predvolená)" : Name)}";
}

/// <summary>Keyboards and languages of input for every new profile, the first is the default one.</summary>
public sealed class KeyboardsElement : Element
{
    /// <summary>Languages as Windows writes them (e.g. sk-SK, cs-CZ, en-US, de-DE, fr-FR, ru-RU).</summary>
    public List<string> Languages { get; set; } = [];

    /// <summary>Another layout for a language than its usual one, e.g. "sk-SK": "0001041B" (Slovak QWERTY).</summary>
    public Dictionary<string, string>? Layouts { get; set; }

    public override string Key => "keyboards";

    public override string Title => "klávesnice " + string.Join(", ", Languages);
}

public enum SessionCleaning
{
    /// <summary>The profile is deleted after every sign-out (and at start, if a sign-out was cut by a power loss).</summary>
    AtSignOut,

    /// <summary>The profile stays the whole day and is deleted at the first chance after midnight.</summary>
    EndOfDay,

    /// <summary>The profile stays the whole week (until Monday).</summary>
    EndOfWeek,

    /// <summary>Not cleaned.</summary>
    Never,
}

/// <summary>Rules of the session of a shared account: when the agent deletes its profile, so that the next pupil starts
/// clean. Only a standard account – an account of an administrator is never cleaned.</summary>
public sealed class SessionElement : Element
{
    /// <summary>The local account the pupils sign in with (e.g. ziak).</summary>
    public string Account { get; set; } = "";

    public SessionCleaning Clean { get; set; } = SessionCleaning.AtSignOut;

    public override string Key => "session:" + Account.Trim().ToLowerInvariant();

    public override string Title => $"relácia konta {Account}";
}

/// <summary>Paths in the user part of the registry in one written form (no HKCU, no doubled backslashes).</summary>
public static class UserRegistryPath
{
    public static string Normalize(string path) =>
        string.Join('\\', path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Paths of the registry in one written form.</summary>
public static class RegistryPath
{
    /// <summary>"HKEY_LOCAL_MACHINE\…" and "hklm\…" become "HKLM\…"; doubled and trailing backslashes go away.</summary>
    public static string Normalize(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "";
        }

        var root = parts[0].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => "HKLM",
            var other => other,
        };
        return string.Join('\\', parts.Skip(1).Prepend(root));
    }

    /// <summary>The part after HKLM\ (for opening the key), or null when the path is not in HKLM.</summary>
    public static string? UnderLocalMachine(string path)
    {
        var normal = Normalize(path);
        return normal.StartsWith(@"HKLM\", StringComparison.Ordinal) ? normal[5..] : null;
    }
}
