using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>Shortcuts of the School store's programs: finding the program from its entry, the name of the shortcut, and
/// on Windows the shortcut written and read back through the shell.</summary>
public static class ShortcutChecks
{
    [Check]
    public static void TheProgramIsFoundFromItsIconAndTheShortcutIsNamed()
    {
        Expect.Equal(@"C:\Program Files\Audacity 4\bin\Audacity.exe", ProgramMatch.IconExe("\"C:\\Program Files\\Audacity 4\\bin\\Audacity.exe\",0"), "v úvodzovkách s číslom");
        Expect.Equal(@"C:\Apps\x.exe", ProgramMatch.IconExe(@"C:\Apps\x.exe,0"), "s číslom");
        Expect.Equal(@"C:\Apps\x.exe", ProgramMatch.IconExe(@"C:\Apps\x.exe"), "len cesta");
        Expect.True(ProgramMatch.IconExe(@"C:\Apps\x.ico") is null && ProgramMatch.IconExe(null) is null, "ikona nie je program");

        Expect.Equal("Audacity.lnk", StoreRules.ShortcutFile("Audacity"), "meno odkazu");
        Expect.Equal("Kreslenie- obrázky.lnk", StoreRules.ShortcutFile("Kreslenie: obrázky"), "bez znakov, ktoré meno súboru nesmie mať");
        Expect.Equal("program.lnk", StoreRules.ShortcutFile(" ... "), "prázdne meno");

        var install = new StoreInstall { Item = "audacity", Title = "Audacity", Account = "ziak", InstalledUtc = DateTimeOffset.UnixEpoch, Program = new ProgramElement { DisplayName = "Audacity", Match = NameMatch.Prefix, Winget = "Audacity.Audacity" } };
        var again = StoreRules.InstallFromJson(StoreRules.ToJson(install));
        Expect.Equal("Audacity", again?.Title, "záznam obchodu si pamätá meno odkazu");
        Expect.Equal("", StoreRules.InstallFromJson(StoreRules.ToJson(new StoreInstall { Item = "x", Account = "a" }))?.Title, "starý záznam bez mena");
    }

    [Check]
    public static void WindowsWritesAndReadsTheShortcut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var file = Path.Combine(Path.GetTempPath(), $"polaris-odkaz-{Guid.NewGuid():N}.lnk");
        try
        {
            Polaris.Windows.Shortcuts.Create(file, notepad);
            Expect.Equal(notepad.ToLowerInvariant(), Polaris.Windows.Shortcuts.Target(file).ToLowerInvariant(), "Windows číta cieľ odkazu");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
