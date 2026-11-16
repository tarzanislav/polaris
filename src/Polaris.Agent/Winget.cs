using System.Runtime.Versioning;

namespace Polaris.Agent;

/// <summary>The Windows package manager. Run by the admin it is found through its alias in the user's profile; run by the
/// system it is found in the folder of installed apps (the newest version).</summary>
[SupportedOSPlatform("windows")]
public static class Winget
{
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(60);

    public static string Find()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias))
        {
            return alias;
        }

        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        try
        {
            var newest = Directory.EnumerateDirectories(apps, "Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe")
                .Select(d => (Dir: d, Version: VersionOf(Path.GetFileName(d))))
                .Where(x => x.Version is not null && File.Exists(Path.Combine(x.Dir, "winget.exe")))
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            if (newest.Dir is not null)
            {
                return Path.Combine(newest.Dir, "winget.exe");
            }
        }
        catch (UnauthorizedAccessException)
        {
            // an admin without the system's rights cannot list this folder – the alias above is the way then
        }
        catch (DirectoryNotFoundException)
        {
        }

        throw new FileNotFoundException("WinGet (Inštalátor aplikácie) sa na tomto PC nenašiel. Nainštaluj ho z Microsoft Store alebo použi installer zo zásoby.");
    }

    /// <summary>"Microsoft.DesktopAppInstaller_1.27.440.0_x64__8wekyb3d8bbwe" → 1.27.440.0.</summary>
    public static Version? VersionOf(string folderName)
    {
        var parts = folderName.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var v) ? v : null;
    }

    public static IReadOnlyList<string> InstallArguments(string id, string? version)
    {
        var args = new List<string>
        {
            "install", "--id", id, "--exact", "--source", "winget", "--scope", "machine", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
        };
        if (version is { Length: > 0 })
        {
            args.Add("--version");
            args.Add(version);
        }

        return args;
    }

    public static IReadOnlyList<string> UpgradeArguments(string id, string? version)
    {
        var args = new List<string>
        {
            "upgrade", "--id", id, "--exact", "--source", "winget", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
        };
        if (version is { Length: > 0 })
        {
            args.Add("--version");
            args.Add(version);
        }

        return args;
    }

    public static IReadOnlyList<string> UninstallArguments(string id) =>
        ["uninstall", "--id", id, "--exact", "--source", "winget", "--silent", "--accept-source-agreements", "--disable-interactivity"];
}
