using System.Runtime.Versioning;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>Shortcuts of the store's programs in the Start menu of all users (folder "Školský obchod"). The program is
/// found through its entry in the installed programs: its icon (DisplayIcon) when that is an .exe, otherwise the .exe in
/// its folder that carries its name.</summary>
[SupportedOSPlatform("windows")]
public sealed class StartMenuShortcuts(Func<List<InstalledProgram>> installed) : IStoreShortcuts
{
    public StartMenuShortcuts()
        : this(InstalledPrograms.Read)
    {
    }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), StoreRules.ShortcutFolder);

    public static string PathOf(string title) => Path.Combine(Folder, StoreRules.ShortcutFile(title));

    public string? Create(StoreInstall install)
    {
        var entry = installed().FirstOrDefault(p => ProgramMatch.Matches(install.Program, p.DisplayName));
        if (entry is null || Program(entry) is not { } exe)
        {
            return "program sa nenašiel na disku – odkaz v ponuke Štart nevznikol";
        }

        Directory.CreateDirectory(Folder);
        var title = install.Title.Length > 0 ? install.Title : entry.DisplayName;
        Polaris.Windows.Shortcuts.Create(PathOf(title), exe);
        return null;
    }

    public void Remove(StoreInstall install)
    {
        if (install.Title.Length > 0 && File.Exists(PathOf(install.Title)))
        {
            File.Delete(PathOf(install.Title));
        }

        if (Directory.Exists(Folder) && !Directory.EnumerateFileSystemEntries(Folder).Any())
        {
            Directory.Delete(Folder);
        }
    }

    /// <summary>The program to start: the DisplayIcon .exe, else the .exe in the install folder named like the program
    /// (never an uninstaller, updater or crash reporter).</summary>
    public static string? Program(InstalledProgram p)
    {
        if (ProgramMatch.IconExe(p.DisplayIcon) is { } icon && File.Exists(icon))
        {
            return icon;
        }

        var folder = p.InstallLocation?.Trim().Trim('"');
        if (folder is not { Length: > 3 } || !Directory.Exists(folder))
        {
            return null;
        }

        var word = new string(p.DisplayName.TakeWhile(char.IsLetterOrDigit).ToArray());
        var candidates = Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 })
            .Where(f => !Helper(Path.GetFileNameWithoutExtension(f)))
            .ToList();
        return candidates.FirstOrDefault(f => word.Length > 1 && Path.GetFileNameWithoutExtension(f).StartsWith(word, StringComparison.OrdinalIgnoreCase))
            ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    private static bool Helper(string name) =>
        name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
        || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("update", StringComparison.OrdinalIgnoreCase)
        || name.Contains("crash", StringComparison.OrdinalIgnoreCase)
        || name.Contains("setup", StringComparison.OrdinalIgnoreCase);
}
