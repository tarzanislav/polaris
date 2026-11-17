using System.Buffers.Binary;
using System.Diagnostics;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>Shortcuts of the School store's programs: the own .lnk writer, finding the program from its entry, the name
/// of the shortcut. The Windows check lets Windows itself read the shortcut back.</summary>
public static class ShortcutChecks
{
    [Check]
    public static void AShortcutCarriesItsTarget()
    {
        foreach (var target in new[] { @"C:\Program Files\Audacity 4\bin\Audacity.exe", @"D:\Škola\Kreslenie\program.exe" })
        {
            var link = ShellLink.Create(target);
            Expect.Equal(0x4Cu, BinaryPrimitives.ReadUInt32LittleEndian(link), "hlavička");
            Expect.Equal(target, ShellLink.Target(link), "cieľ prečítaný späť: " + target);
            Expect.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(link.AsSpan(link.Length - 4)), "na konci ukončovací blok");
        }

        Expect.Throws<ArgumentException>(() => ShellLink.Create("program.exe"), "len úplná cesta");
        Expect.True(ShellLink.Target(new byte[200]) is null, "cudzie bajty nie sú odkaz");
    }

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
    public static void WindowsReadsTheShortcut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var file = Path.Combine(Path.GetTempPath(), $"polaris-odkaz-{Guid.NewGuid():N}.lnk");
        try
        {
            File.WriteAllBytes(file, ShellLink.Create(notepad));
            using var shell = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-Command", $"(New-Object -ComObject WScript.Shell).CreateShortcut('{file}').TargetPath" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var seen = shell.StandardOutput.ReadToEnd().Trim();
            shell.WaitForExit();
            Expect.Equal(notepad.ToLowerInvariant(), seen.ToLowerInvariant(), "Windows číta cieľ odkazu");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
