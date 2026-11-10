using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polaris.Core;

namespace Polaris.Net;

/// <summary>Everything the centre keeps about the classroom besides its log: the PCs, groups and consoles, the timetable
/// and the profiles with their versions. One file, one schema number.</summary>
public sealed class CentreData
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    public DateTimeOffset SavedUtc { get; set; }

    public Fleet Fleet { get; set; } = new();

    public Timetable Timetable { get; set; } = new();

    public List<Profile> Profiles { get; set; } = [];

    public Profile? FindProfile(string name) => Profiles.FirstOrDefault(p => p.Name == name);
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CentreData))]
internal sealed partial class CentreDataJson : JsonSerializerContext;

/// <summary>Saves and loads <see cref="CentreData"/>. A save writes a new file first and only then replaces the old one,
/// so a power loss leaves the old or the new file, never half of one. Before each save the previous file is kept as a
/// numbered backup (the newest ten).</summary>
public sealed class CentreDataStore(string file)
{
    public const int KeepBackups = 10;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public string File { get; } = file;

    public string BackupDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(File))!, "zalohy");

    public static string ToJson(CentreData data) => JsonSerializer.Serialize(data, CentreDataJson.Default.CentreData);

    public static CentreData FromJson(string json)
    {
        var data = JsonSerializer.Deserialize(json, CentreDataJson.Default.CentreData)
            ?? throw new InvalidDataException("Súbor nastavení je prázdny.");
        if (data.Schema > CentreData.CurrentSchema)
        {
            throw new InvalidDataException(
                $"Súbor nastavení je z novšieho programu (schéma {data.Schema}, tento program pozná {CentreData.CurrentSchema}).");
        }

        return data;
    }

    /// <summary>Loads the data; a missing file gives empty data. A damaged file falls back to the newest backup that
    /// reads well, and says so.</summary>
    public (CentreData Data, string? Note) Load()
    {
        if (!System.IO.File.Exists(File))
        {
            return (new CentreData(), null);
        }

        try
        {
            return (FromJson(System.IO.File.ReadAllText(File, Utf8)), null);
        }
        catch (JsonException e)
        {
            foreach (var backup in Backups().Reverse())
            {
                try
                {
                    var data = FromJson(System.IO.File.ReadAllText(backup, Utf8));
                    return (data, $"Súbor nastavení bol poškodený ({e.Message}); načítaná záloha {Path.GetFileName(backup)}.");
                }
                catch (JsonException)
                {
                    // try an older one
                }
            }

            throw new InvalidDataException("Súbor nastavení je poškodený a žiadna záloha sa nedá prečítať.", e);
        }
    }

    public void Save(CentreData data, DateTimeOffset now)
    {
        data.Schema = CentreData.CurrentSchema;
        data.SavedUtc = now;
        var directory = Path.GetDirectoryName(Path.GetFullPath(File))!;
        Directory.CreateDirectory(directory);
        if (System.IO.File.Exists(File))
        {
            Directory.CreateDirectory(BackupDirectory);
            var next = NextBackupNumber();
            System.IO.File.Copy(File, Path.Combine(BackupDirectory, $"nastavenia-zaloha-{next:D4}.json"));
            foreach (var old in Backups().SkipLast(KeepBackups))
            {
                System.IO.File.Delete(old);
            }
        }

        var temp = File + ".novy";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(Utf8.GetBytes(ToJson(data)));
            stream.Flush(flushToDisk: true);
        }

        System.IO.File.Move(temp, File, overwrite: true);
    }

    /// <summary>The backups, oldest first.</summary>
    public IEnumerable<string> Backups() =>
        Directory.Exists(BackupDirectory)
            ? Directory.GetFiles(BackupDirectory, "nastavenia-zaloha-*.json").OrderBy(f => f, StringComparer.Ordinal)
            : [];

    private int NextBackupNumber()
    {
        var last = Backups().LastOrDefault();
        if (last is null)
        {
            return 1;
        }

        var name = Path.GetFileNameWithoutExtension(last);
        return int.TryParse(name.AsSpan(name.LastIndexOf('-') + 1), out var n) ? n + 1 : 1;
    }
}
