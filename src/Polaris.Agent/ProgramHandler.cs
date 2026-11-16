using System.Runtime.Versioning;
using Microsoft.Win32;
using Polaris.Install;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>An entry of the list of installed programs (the machine part, both the 64- and the 32-bit view).</summary>
public sealed record InstalledProgram(
    string KeyName,
    string DisplayName,
    string? Version,
    string? UninstallString,
    string? QuietUninstallString,
    bool WindowsInstaller);

[SupportedOSPlatform("windows")]
public static class InstalledPrograms
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledProgram> Read()
    {
        var list = new List<InstalledProgram>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = root.OpenSubKey(UninstallKey);
            if (uninstall is null)
            {
                continue;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") is not string display || display.Trim().Length == 0)
                {
                    continue;
                }

                list.Add(new InstalledProgram(
                    name,
                    display.Trim(),
                    entry.GetValue("DisplayVersion") as string,
                    entry.GetValue("UninstallString") as string,
                    entry.GetValue("QuietUninstallString") as string,
                    entry.GetValue("WindowsInstaller") is int wi && wi == 1));
            }
        }

        return list;
    }
}

/// <summary>Programs for the whole PC: recognised in the list of installed programs, installed by WinGet or by a checked
/// installer from the store, removed by WinGet or by the silent uninstaller Windows knows.</summary>
[SupportedOSPlatform("windows")]
public sealed class ProgramHandler(string recipeFolder, string? supply = null) : IElementHandler, IReadsInBatches
{
    public static readonly TimeSpan InstallerLimit = TimeSpan.FromMinutes(60);

    private readonly Lock _gate = new();
    private List<InstalledProgram>? _snapshot;
    private int _readings;

    /// <summary>While a plan is made, the list of installed programs is read once for all its programs.</summary>
    public IDisposable ReadOnce()
    {
        lock (_gate)
        {
            _readings++;
        }

        return new Reading(this);
    }

    private List<InstalledProgram> Installed()
    {
        lock (_gate)
        {
            if (_readings == 0)
            {
                return InstalledPrograms.Read();
            }

            return _snapshot ??= InstalledPrograms.Read();
        }
    }

    private void Forget()
    {
        lock (_gate)
        {
            _snapshot = null;
        }
    }

    private sealed class Reading(ProgramHandler owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (!_done && --owner._readings == 0)
                {
                    owner._snapshot = null;
                }

                _done = true;
            }
        }
    }

    public bool CanHandle(Element element) => element is ProgramElement;

    public Observation Observe(Element element)
    {
        var p = (ProgramElement)element;
        if (p.Package is { } link)
        {
            return ObservePackage(Packages.Resolve(link, recipeFolder, supply));
        }

        var found = Installed().Where(i => ProgramMatch.Matches(p, i.DisplayName)).ToList();
        if (found.Count == 0)
        {
            return new Observation(Observed.Absent, "nie je nainštalovaný");
        }

        var first = found[0];
        var detail = first.Version is { Length: > 0 } v ? $"{first.DisplayName}, verzia {v}" : first.DisplayName;
        detail = found.Count > 1 ? $"{detail} a ďalšie ({found.Count})" : detail;
        if (p.Present && !found.Any(i => ProgramMatch.VersionMatches(p.Version, i.Version)))
        {
            return new Observation(Observed.Different, $"{detail}, má byť verzia {p.Version}");
        }

        return new Observation(Observed.Present, detail);
    }

    public void Apply(Element element)
    {
        var p = (ProgramElement)element;
        try
        {
            if (p.Present)
            {
                Install(p);
            }
            else
            {
                Remove(p);
            }
        }
        finally
        {
            Forget();
        }
    }

    private Observation ObservePackage(Package package)
    {
        if (package.Kind == PackageKind.Msi)
        {
            var (installed, version) = Msi.State(package.ProductCode!);
            return installed
                ? new Observation(Observed.Present, $"{package.Title} {version}".Trim())
                : new Observation(Observed.Absent, "nie je nainštalovaný");
        }

        var name = new ProgramElement { DisplayName = package.DisplayName, Match = package.Match };
        var found = Installed().FirstOrDefault(i => ProgramMatch.Matches(name, i.DisplayName));
        return found is null
            ? new Observation(Observed.Absent, "nie je nainštalovaný")
            : new Observation(Observed.Present, found.Version is { Length: > 0 } v ? $"{found.DisplayName}, verzia {v}" : found.DisplayName);
    }

    private void InstallPackage(ProgramElement p, PackageRef link)
    {
        var package = Packages.Resolve(link, recipeFolder, supply);
        var installer = Packages.Fetch(package, recipeFolder, supply);
        if (package.Kind == PackageKind.Msi)
        {
            var log = Packages.LogOf(package.Id);
            var result = Msi.Install(installer, package.Arguments, log);
            if (!result.Ok)
            {
                throw new InvalidOperationException($"Windows Installer: {result.Message} (kód {result.Code}). {Msi.Why(log)}".Trim());
            }

            return;
        }

        Finish(p, Processes.RunRaw(installer, package.Arguments, InstallerLimit), "Inštalácia skončila");
    }

    private void RemovePackage(PackageRef link)
    {
        var package = Packages.Resolve(link, recipeFolder, supply);
        if (package.Kind == PackageKind.Msi)
        {
            var result = Msi.Remove(package.ProductCode!, Packages.LogOf(package.Id));
            if (!result.Ok && result.Code != 1605 /* not installed */)
            {
                throw new InvalidOperationException($"Windows Installer: {result.Message} (kód {result.Code}).");
            }

            return;
        }

        Remove(new ProgramElement
        {
            DisplayName = package.DisplayName,
            Match = package.Match,
            Present = false,
            Installer = new InstallerSource { UninstallArguments = package.UninstallArguments },
        });
    }

    private void Install(ProgramElement p)
    {
        if (p.Package is { } link)
        {
            InstallPackage(p, link);
            return;
        }

        ProcessResult result;
        if (p.Winget is { Length: > 0 } wid && Observe(p).State == Observed.Different)
        {
            // another version: upgrade; when WinGet cannot get there (an older version), remove and install the wanted one
            result = Processes.Run(Winget.Find(), Winget.UpgradeArguments(wid, p.Version), Winget.Limit);
            if (Observe(p).State == Observed.Present)
            {
                return;
            }

            Finish(new ProgramElement { DisplayName = p.DisplayName, Match = p.Match, Winget = wid, Present = false },
                Processes.Run(Winget.Find(), Winget.UninstallArguments(wid), Winget.Limit), "Odinštalovanie starej verzie skončilo");
            result = Processes.Run(Winget.Find(), Winget.InstallArguments(wid, p.Version), Winget.Limit);
        }
        else if (p.Winget is { Length: > 0 } id)
        {
            result = Processes.Run(Winget.Find(), Winget.InstallArguments(id, p.Version), Winget.Limit);
        }
        else
        {
            var installer = p.Installer ?? throw new InvalidDataException("Program nemá zdroj inštalácie.");
            var folder = Path.GetFullPath(recipeFolder);
            var file = Path.GetFullPath(Path.Combine(folder, installer.File));
            if (!file.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Inštalátor {installer.File} nie je vnútri priečinka receptu – nespustil som ho.");
            }

            if (!File.Exists(file))
            {
                throw new FileNotFoundException($"Inštalátor {installer.File} sa v priečinku receptu nenašiel.");
            }

            var actual = RecipeText.FileFingerprint(file);
            if (!actual.Equals(installer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Inštalátor {installer.File} má iný odtlačok, než je v recepte – nespustil som ho (je {actual[..12]}…).");
            }

            if (file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                var msi = Msi.Install(file, installer.Arguments, Packages.LogOf(Path.GetFileNameWithoutExtension(file)));
                if (!msi.Ok && Observe(p).State != Observed.Present)
                {
                    throw new InvalidOperationException($"Windows Installer: {msi.Message} (kód {msi.Code}).");
                }

                return;
            }

            result = Processes.RunRaw(file, installer.Arguments, InstallerLimit);
        }

        Finish(p, result, "Inštalácia skončila");
    }

    private void Remove(ProgramElement p)
    {
        if (p.Package is { } link)
        {
            RemovePackage(link);
            return;
        }

        if (p.Winget is { Length: > 0 } id)
        {
            Finish(p, Processes.Run(Winget.Find(), Winget.UninstallArguments(id), Winget.Limit), "Odinštalovanie skončilo");
            return;
        }

        foreach (var entry in Installed().Where(i => ProgramMatch.Matches(p, i.DisplayName)))
        {
            ProcessResult result;
            if (ProgramMatch.MsiProductCode(entry.KeyName, entry.UninstallString) is { } code
                && (entry.WindowsInstaller || entry.UninstallString?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) == true))
            {
                var msi = Msi.Remove(code, Packages.LogOf(code.Trim('{', '}')));
                if (!msi.Ok && msi.Code != 1605)
                {
                    throw new InvalidOperationException($"Odinštalovanie {entry.DisplayName}: {msi.Message} (kód {msi.Code}).");
                }

                continue;
            }
            else if (ProgramMatch.SplitCommand(entry.QuietUninstallString) is { } quiet)
            {
                result = Processes.RunRaw(quiet.Program, quiet.Arguments, InstallerLimit);
            }
            else if (p.Installer?.UninstallArguments is { Length: > 0 } extra && ProgramMatch.SplitCommand(entry.UninstallString) is { } plain)
            {
                result = Processes.RunRaw(plain.Program, $"{plain.Arguments} {extra}".Trim(), InstallerLimit);
            }
            else
            {
                throw new InvalidOperationException(
                    $"{entry.DisplayName} nemá tichú odinštaláciu – doplň do receptu installer.uninstallArguments (napr. /S).");
            }

            if (!Accepted(result.ExitCode))
            {
                throw new InvalidOperationException($"Odinštalovanie {entry.DisplayName} skončilo kódom {result.Code}. {result.OutputTail}");
            }
        }
    }

    /// <summary>A program may end with a non-zero code and still have done its work (for example "already installed");
    /// the state decides, the code only explains a failure.</summary>
    private void Finish(ProgramElement p, ProcessResult result, string ended)
    {
        if (Accepted(result.ExitCode))
        {
            return;
        }

        var now = Observe(p).State;
        if ((p.Present && now == Observed.Present) || (!p.Present && now == Observed.Absent))
        {
            return;
        }

        throw new InvalidOperationException($"{ended} kódom {result.Code}. {result.OutputTail}".Trim());
    }

    /// <summary>0 = done; 3010 = done, a restart is needed; 1641 = done, the installer starts a restart.</summary>
    private static bool Accepted(int code) => code is 0 or 3010 or 1641;
}
