using Polaris.Core;
using Polaris.Net;

namespace Polaris.Tests;

public static class CentreDataChecks
{
    private static readonly DateTimeOffset T = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "polaris-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static CentreData Sample()
    {
        var profile = new Profile { Name = "KANCELARSKY", Description = "kancelária" };
        var v1 = profile.NewDraft("prvá", T);
        v1.Apps = [new InstalledApp("LibreOffice", "25.8")];
        profile.Approve(v1.Number, new string('a', 64));
        return new CentreData
        {
            Fleet = new Fleet
            {
                Groups = [new Group { Name = "novsie", DefaultProfile = "KANCELARSKY" }],
                Pcs = [new Pc { Id = Guid.NewGuid(), Name = "PC 01", Group = "novsie" }],
                Consoles = [new ConsoleAccess { Fingerprint = "ABCD", Name = "Správca", Role = Role.Admin, PublicKey = [1, 2, 3] }],
            },
            Timetable = new Timetable
            {
                Periods = [new Period(1, new TimeOnly(8, 0), new TimeOnly(8, 45))],
                Lessons = [new Lesson(1, 1, "novsie", "KANCELARSKY")],
                Overrides = [new Override(new DateOnly(2026, 11, 16), 1, "novsie", "KANCELARSKY")],
                DaysOff = [new DateOnly(2026, 11, 17)],
            },
            Profiles = [profile],
        };
    }

    [Check]
    public static void EverythingSurvivesSaveAndLoad()
    {
        var dir = TempDir();
        try
        {
            var store = new CentreDataStore(Path.Combine(dir, "nastavenia.json"));
            store.Save(Sample(), T);
            var (back, note) = store.Load();
            Expect.True(note is null, "bez poznámky");
            Expect.Equal("PC 01", back.Fleet.Pcs.Single().Name, "PC");
            Expect.Equal(Role.Admin, back.Fleet.Consoles.Single().Role, "rola");
            Expect.Equal(new TimeOnly(8, 45), back.Timetable.Periods.Single().End, "zvonenie");
            Expect.True(back.Timetable.DaysOff.Contains(new DateOnly(2026, 11, 17)), "voľný deň");
            Expect.Equal("KANCELARSKY", back.Timetable.Overrides.Single().Profile, "jednorazová zmena");
            var p = back.FindProfile("KANCELARSKY")!;
            Expect.Equal(VersionState.Approved, p.Get(1)!.State, "stav verzie");
            Expect.Equal("25.8", p.Get(1)!.Apps.Single().Version, "program vo verzii");
            Expect.Equal(T, back.SavedUtc, "čas uloženia");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void MissingFileGivesEmptyData()
    {
        var dir = TempDir();
        try
        {
            var (data, note) = new CentreDataStore(Path.Combine(dir, "nastavenia.json")).Load();
            Expect.True(data.Fleet.Pcs.Count == 0 && note is null, "prázdne nastavenia");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void NumberedBackupsKeepTheNewestTen()
    {
        var dir = TempDir();
        try
        {
            var store = new CentreDataStore(Path.Combine(dir, "nastavenia.json"));
            for (var i = 0; i < 14; i++)
            {
                store.Save(Sample(), T.AddMinutes(i));
            }

            var names = store.Backups().Select(Path.GetFileName).ToList();
            Expect.Equal(CentreDataStore.KeepBackups, names.Count, "desať záloh");
            Expect.Equal("nastavenia-zaloha-0004.json", names[0], "najstaršia ponechaná");
            Expect.Equal("nastavenia-zaloha-0013.json", names[^1], "najnovšia");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void DamagedFileFallsBackToBackup()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "nastavenia.json");
            var store = new CentreDataStore(file);
            store.Save(Sample(), T);
            store.Save(Sample(), T.AddMinutes(1)); // the first save is now a backup
            var text = File.ReadAllText(file);
            File.WriteAllText(file, text[..(text.Length / 2)]); // cut in half
            var (data, note) = store.Load();
            Expect.True(note is not null && note.Contains("záloha", StringComparison.Ordinal), "hlási zálohu");
            Expect.Equal("PC 01", data.Fleet.Pcs.Single().Name, "údaje zo zálohy");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void NewerSchemaIsRefused()
    {
        var json = CentreDataStore.ToJson(Sample()).Replace("\"schema\": 1", "\"schema\": 99", StringComparison.Ordinal);
        Expect.Throws<InvalidDataException>(() => CentreDataStore.FromJson(json), "súbor z novšieho programu");
    }
}
