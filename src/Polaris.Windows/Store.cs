using Polaris.Core;

namespace Polaris.Windows;

/// <summary>The store (Sklad) on the host volume: the layers, the state and the journal. Seen as C:\POLARIS\Sklad from
/// the original Windows and under another letter from a layer.</summary>
public sealed class Store(string directory)
{
    public const string RelativeDirectory = @"POLARIS\Sklad";
    private const string LockName = "POLARIS-Lab-Stav";

    public string Directory { get; } = directory;

    /// <summary>C:\POLARIS – the root of everything POLARIS put on the host volume.</summary>
    public string Root => Path.GetDirectoryName(Directory)!;

    /// <summary>The drive letter of the host volume as this Windows sees it ("C:").</summary>
    public string DriveLetter => Path.GetPathRoot(Directory)!.TrimEnd('\\');

    public string StateFile => Path.Combine(Directory, "stav.json");

    public string JournalFile => Path.Combine(Directory, "dennik.jsonl");

    public string ReportsDirectory => Path.Combine(Directory, "Spravy");

    public string SlotPath(Slot slot) => Path.Combine(Directory, Slots.FileName(slot));

    /// <summary>The path of a layer as the boot manager sees it on its volume: \POLARIS\Sklad\relacia-A.vhdx.</summary>
    public static string BootPath(string file) => @"\" + RelativeDirectory + @"\" + file;

    public bool Exists => File.Exists(StateFile) || File.Exists(StateFile + ".bak");

    /// <summary>Finds the store on any local drive (from a layer the host volume has another letter).</summary>
    public static Store? Find()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            var dir = Path.Combine(drive.RootDirectory.FullName, RelativeDirectory);
            var store = new Store(dir);
            if (store.Exists)
            {
                return store;
            }
        }

        return null;
    }

    public static Store OnSystemDrive() =>
        new(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, RelativeDirectory));

    public LabState Load()
    {
        foreach (var file in new[] { StateFile, StateFile + ".bak" })
        {
            try
            {
                if (File.Exists(file))
                {
                    return Serialization.StateFromJson(File.ReadAllText(file));
                }
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                // a state cut by a power loss: the previous one follows
            }
        }

        throw new FileNotFoundException("Stav POLARIS sa nenašiel.", StateFile);
    }

    /// <summary>Writes the state so that a power loss leaves either the old or the new one, never half.</summary>
    public void Save(LabState state)
    {
        var temp = StateFile + ".novy";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = Serialization.Utf8.GetBytes(Serialization.ToJson(state));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(StateFile))
        {
            File.Copy(StateFile, StateFile + ".bak", overwrite: true);
        }

        File.Move(temp, StateFile, overwrite: true);
    }

    public JournalEntry Append(LabState state, string kind, string text, Slot? slot = null, BootRecord? boot = null, int? percent = null, int? windowsBootMs = null)
    {
        var entry = new JournalEntry
        {
            Seq = ++state.JournalSeq,
            Utc = DateTimeOffset.UtcNow,
            Kind = kind,
            Text = text,
            Slot = slot,
            Boot = boot,
            Percent = percent,
            WindowsBootMs = windowsBootMs,
        };
        using var stream = new FileStream(JournalFile, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(Serialization.Utf8.GetBytes(Serialization.ToLine(entry) + "\n"));
        stream.Flush(flushToDisk: true);
        return entry;
    }

    public List<JournalEntry> Journal() =>
        File.Exists(JournalFile) ? Serialization.ReadJournal(File.ReadAllText(JournalFile)) : [];

    /// <summary>Loads, changes and saves the state under the system lock.</summary>
    public T Update<T>(Func<LabState, T> change)
    {
        using var guard = new SystemLock(LockName);
        var state = Load();
        var result = change(state);
        Save(state);
        return result;
    }

    public void Update(Action<LabState> change) => Update(s =>
    {
        change(s);
        return 0;
    });

    /// <summary>Writes the report into the store (it survives every restart) and returns its path.</summary>
    public string WriteReport(LabState state)
    {
        System.IO.Directory.CreateDirectory(ReportsDirectory);
        var file = Path.Combine(ReportsDirectory, $"POLARIS-{Product.Stage}-sprava-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(file, Report.Build(state, Journal(), DateTimeOffset.UtcNow), Serialization.Utf8);
        return file;
    }
}
