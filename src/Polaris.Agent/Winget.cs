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

    /// <summary>Runs WinGet. Found in the folder of installed apps (as the system finds it), winget.exe is started
    /// outside its package, and Windows does not then give it the frameworks it is built on: without them it ends at once
    /// with 0xC0000135 (a DLL not found). Their folders go first into its PATH.</summary>
    public static ProcessResult Run(IReadOnlyList<string> arguments)
    {
        var program = Find();
        return Processes.Run(program, arguments, Limit, Frameworks(program));
    }

    /// <summary>The folders of the newest 64-bit C++ runtime frameworks of apps (VCLibs), and WinGet's own folder.</summary>
    public static List<string> Frameworks(string program)
    {
        var folders = new List<string> { Path.GetDirectoryName(program)! };
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        foreach (var pattern in new[] { "Microsoft.VCLibs.140.00.UWPDesktop_*_x64__8wekyb3d8bbwe", "Microsoft.VCLibs.140.00_*_x64__8wekyb3d8bbwe" })
        {
            try
            {
                var newest = Directory.EnumerateDirectories(apps, pattern)
                    .Select(d => (Dir: d, Version: VersionOf(Path.GetFileName(d))))
                    .Where(x => x.Version is not null)
                    .OrderByDescending(x => x.Version)
                    .FirstOrDefault();
                if (newest.Dir is not null)
                {
                    folders.Add(newest.Dir);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // an admin cannot list the folder; started through its alias WinGet has its frameworks anyway
            }
        }

        return folders;
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
