using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Polaris.Install;
using Polaris.Recipes;
using Polaris.Windows;

namespace Polaris.Agent;

/// <summary>Drivers of a PC: what is missing, a backup of a hardware build from a reference PC, and installing from such
/// a backup on a freshly installed PC. Only signed packages; every file checked by its fingerprint in the agent's
/// protected folder before Windows sees it.</summary>
[SupportedOSPlatform("windows")]
public static class DriverWork
{
    public static string Staging => Path.Combine(Packages.AgentFolder, "ovladace");

    /// <summary>Every id this PC's devices answer to (hardware and compatible ids).</summary>
    public static HashSet<string> PresentIds(IEnumerable<DeviceInfo> devices) =>
        devices.SelectMany(d => d.HardwareIds.Concat(d.CompatibleIds)).Select(i => i.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>Copies the third-party packages of this PC (those its present devices use, or all) into an empty folder
    /// and writes ovladace.json with every file's fingerprint.</summary>
    public static DriverBackup Backup(string target, string name, bool all, TextWriter output, string hardware = "", string edition = "")
    {
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new InvalidDataException($"Priečinok {target} nie je prázdny – záloha ide vždy do nového alebo prázdneho priečinka.");
        }

        var devices = Devices.Present();
        var usedBy = devices.Where(d => d.InfPath is { Length: > 0 })
            .GroupBy(d => d.InfPath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var backup = new DriverBackup
        {
            Name = name,
            CreatedUtc = DateTimeOffset.UtcNow,
            Windows = Environment.OSVersion.Version.ToString(3),
            Hardware = hardware,
            Edition = edition,
        };
        Directory.CreateDirectory(target);
        if (DriverPlace.OnThisPc(target) is not null)
        {
            try
            {
                // on a disk of this PC (only when the administrator asked for it): only the system and administrators –
                // a pupil neither sees nor deletes the backup. On a stick or a shared folder the rights stay as they are
                // (the administrators of this PC are not those of the server).
                Polaris.Windows.Security.Protect(target);
            }
            catch (Polaris.Windows.SystemCallException)
            {
            }
        }

        var index = 0;
        foreach (var published in DriverStore.ThirdParty())
        {
            var users = usedBy.GetValueOrDefault(published) ?? [];
            if (!all && users.Count == 0)
            {
                continue;
            }

            string source;
            try
            {
                source = DriverStore.Location(published);
            }
            catch (Polaris.Windows.SystemCallException e)
            {
                output.WriteLine($"  ! {published}: {e.Message} – preskočený");
                continue;
            }

            index++;
            var entry = new DriverPackageEntry { Folder = DriverBackupFormat.FolderName(index, Path.GetFileName(source)), Inf = Path.GetFileName(source) };
            var from = Path.GetDirectoryName(source)!;
            var to = Path.Combine(target, entry.Folder);
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(from, file);
                var copy = Path.Combine(to, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
                entry.Files.Add(new DriverFile(relative.Replace('\\', '/'), RecipeText.FileFingerprint(copy), new FileInfo(copy).Length));
            }

            var inf = InfFile.Load(File.ReadAllBytes(Path.Combine(to, entry.Inf)));
            var (date, version) = inf.DriverVer;
            entry.Class = inf.Class ?? "";
            entry.Provider = inf.Provider ?? "";
            entry.Version = version;
            entry.Date = date;
            entry.SupportedIds = [.. inf.HardwareIds()];
            entry.Devices = [.. users.Select(d => new DriverDevice(d.Name, d.HardwareIds))];
            if (inf.Catalog is { Length: > 0 } catalog && entry.Files.FirstOrDefault(f => string.Equals(f.Path, catalog, StringComparison.OrdinalIgnoreCase)) is { } cat)
            {
                entry.Catalog = cat.Path;
                var signature = Authenticode.Check(Path.Combine(to, cat.Path));
                entry.Signer = signature.Kind == SignatureKind.Valid ? signature.Publisher ?? "" : "";
            }

            backup.Packages.Add(entry);
            output.WriteLine($"  + {entry.Folder}: {entry.Title}{(users.Count > 0 ? " – " + string.Join(", ", users.Select(u => u.Name).Distinct().Take(3)) : "")}");
        }

        File.WriteAllText(Path.Combine(target, DriverBackup.FileName), DriverBackupFormat.ToJson(backup), new UTF8Encoding(false));
        return backup;
    }

    public sealed record Installed(DriverPackageEntry Package, bool Ok, bool Reboot, string Message);

    /// <summary>The packages of a backup this PC needs (they serve one of its devices), or all of them.</summary>
    public static List<DriverPackageEntry> Needed(DriverBackup backup, IEnumerable<DeviceInfo> devices, bool all)
    {
        var ids = PresentIds(devices);
        return all ? [.. backup.Packages] : [.. backup.Packages.Where(p => DriverBackupFormat.Serves(p, ids))];
    }

    /// <summary>One package: copied into the protected folder (only the listed files), checked there, the catalog's
    /// signature checked, then handed to Windows. The copy is removed afterwards.</summary>
    public static Installed Install(string backupFolder, DriverPackageEntry p)
    {
        var stage = Path.Combine(Staging, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            AgentService.ProtectFolder(Packages.AgentFolder);
            foreach (var f in p.Files)
            {
                var relative = f.Path.Replace('/', '\\');
                var copy = Path.Combine(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(Path.Combine(backupFolder, p.Folder, relative), copy);
            }

            if (DriverBackupFormat.VerifyPackage(stage, p).FirstOrDefault() is { } problem)
            {
                return new Installed(p, false, false, problem + " Nenainštaloval som ho.");
            }

            if (p.Catalog.Length == 0)
            {
                return new Installed(p, false, false, "balík nemá katalóg s podpisom – nenainštaloval som ho.");
            }

            var signature = Authenticode.Check(Path.Combine(stage, p.Catalog.Replace('/', '\\')));
            if (signature.Kind != SignatureKind.Valid)
            {
                return new Installed(p, false, false, $"podpis balíka neplatí ({signature.Detail}) – nenainštaloval som ho.");
            }

            var done = DriverStore.Install(Path.Combine(stage, p.Inf));
            return new Installed(p, true, done.Reboot, done.Applied
                ? $"nainštalovaný (podpísal {signature.Publisher})"
                : $"je v úložisku ovládačov; zariadenia už majú rovnako dobrý alebo lepší ovládač (podpísal {signature.Publisher})");
        }
        catch (Exception e) when (e is Polaris.Windows.SystemCallException or IOException or UnauthorizedAccessException)
        {
            return new Installed(p, false, false, e.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(stage, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The result of installing from a backup as one line of the agent's journal.</summary>
    public static RunRecord Record(DriverBackup backup, string fingerprint, DateTimeOffset started, IEnumerable<(Installed Result, double Seconds)> results) => new()
    {
        StartedUtc = started,
        FinishedUtc = DateTimeOffset.UtcNow,
        Recipe = "ovládače " + backup.Name,
        Fingerprint = fingerprint,
        Results = [.. results.Select(r => new ChangeResult(r.Result.Package.Folder, "ovládač " + r.Result.Package.Title, ChangeKind.Install, r.Result.Ok, r.Result.Message, r.Seconds))],
    };

    public static string Describe(DeviceInfo d)
    {
        var driver = d.InfPath is null
            ? "bez ovládača"
            : $"{d.DriverProvider} {d.DriverVersion}{(d.DriverDate is { } date ? " z " + date.ToString("d.M.yyyy", CultureInfo.InvariantCulture) : "")} ({d.InfPath})";
        return $"{d.Name} [{d.Class}] – {driver}";
    }

    public static double Seconds(Stopwatch watch) => Math.Round(watch.Elapsed.TotalSeconds, 1);
}

/// <summary>Where backups of drivers go: the folder of backups (HKLM\SOFTWARE\POLARIS\Agent, value ZalohyOvladacov) set
/// by the administrator – a USB stick or a shared folder; later the console keeps them on its own disk. When none is set,
/// the folder POLARIS-ovladace on the first USB stick. Never a disk of the PC itself unless the administrator says so:
/// the drivers are for other PCs of the build, and a disk of the PC is what a new installation erases.</summary>
[SupportedOSPlatform("windows")]
public static class DriverPlace
{
    public const string Key = @"SOFTWARE\POLARIS\Agent";
    public const string Value = "ZalohyOvladacov";

    public static string? Configured()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Key);
        return key?.GetValue(Value) is string text && text.Trim().Length > 0 ? text.Trim() : null;
    }

    public static void Set(string? folder)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(Key);
        if (folder is { Length: > 0 })
        {
            key.SetValue(Value, folder, Microsoft.Win32.RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(Value, throwOnMissingValue: false);
        }
    }

    /// <summary>The folder of backups and how it was chosen; null when none is set and no USB stick is in.</summary>
    public static (string Folder, string How)? Root()
    {
        if (Configured() is { } set)
        {
            return (set, "nastavený priečinok záloh");
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Removable && drive.IsReady)
                {
                    return (Path.Combine(drive.RootDirectory.FullName, DriverBuilds.DefaultFolderOnStick), $"predvolený priečinok na USB kľúči {drive.Name.TrimEnd('\\')}");
                }
            }
            catch (IOException)
            {
                // a reader without a card
            }
        }

        return null;
    }

    /// <summary>Why the folder is on this PC ("disk C: tohto PC"), or null for a USB stick, a shared folder, a network
    /// drive.</summary>
    public static string? OnThisPc(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        try
        {
            var type = new DriveInfo(root).DriveType;
            return type is DriveType.Fixed or DriveType.Ram
                ? $"disk {root.TrimEnd('\\')} tohto PC"
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Maker and model from the firmware, as Windows keeps them.</summary>
    public static HardwareInfo ThisHardware()
    {
        using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        string Read(string name) => bios?.GetValue(name) as string ?? "";
        return new HardwareInfo(Read("SystemManufacturer"), Read("SystemProductName"), Read("SystemVersion"), Read("BaseBoardManufacturer"), Read("BaseBoardProduct"));
    }

    public static string ThisEdition()
    {
        using var nt = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return DriverBuilds.Edition(nt?.GetValue("ProductName") as string ?? "", Environment.OSVersion.Version.Build);
    }

    /// <summary>The backups in a folder: the folder itself when it is a backup, else its subfolders that are.</summary>
    public static List<FoundBackup> Find(string folder, TextWriter? problems = null)
    {
        var found = new List<FoundBackup>();
        if (File.Exists(Path.Combine(folder, DriverBackup.FileName)))
        {
            found.Add(new FoundBackup(folder, DriverBackupFormat.Load(folder)));
            return found;
        }

        if (!Directory.Exists(folder))
        {
            return found;
        }

        foreach (var sub in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(sub, DriverBackup.FileName)))
            {
                continue;
            }

            try
            {
                found.Add(new FoundBackup(sub, DriverBackupFormat.Load(sub)));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems?.WriteLine($"  – {Path.GetFileName(sub)}: {e.Message}");
            }
        }

        return found;
    }
}
