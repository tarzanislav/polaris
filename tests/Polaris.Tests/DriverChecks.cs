using System.Text;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>Drivers (K5a) on any system: the own reader of INF files and the backup of a hardware build.</summary>
public static class DriverChecks
{
    private const string Inf = """
        ; Skúšobný ovládač v tvare, aký majú skutočné INF
        [Version]
        Signature   = "$WINDOWS NT$"
        Class       = System
        ClassGuid   = {4d36e97d-e325-11ce-bfc1-08002be10318}
        Provider    = %Intel%
        CatalogFile.NTamd64 = heci.cat
        DriverVer   = 07/18/2016,11.0.0.1166 ; dátum a verzia

        [Manufacturer]
        %Intel% = Intel, NTamd64.10.0, NTx86

        [Intel.NTamd64.10.0]
        %HECI_DESC% = HECI_Inst, PCI\VEN_8086&DEV_1E3A, \
                      PCI\VEN_8086&DEV_1C3A
        "Iný popis, s čiarkou" = HECI_Inst, pci\ven_8086&dev_3b64

        [Intel.NTx86]
        %HECI_DESC% = HECI_Inst, PCI\VEN_8086&DEV_9999

        [Intel]
        %HECI_DESC% = HECI_Inst, PCI\VEN_8086&DEV_8888

        [Strings]
        Intel     = "Intel"
        HECI_DESC = "Intel(R) Management Engine Interface; 100%%"
        """;

    [Check]
    public static void InfIsReadLikeWindowsReadsIt()
    {
        var inf = InfFile.Parse(Inf);
        Expect.Equal("System", inf.Class, "trieda");
        Expect.Equal("Intel", inf.Provider, "výrobca cez %reťazec%");
        Expect.Equal("heci.cat", inf.Catalog, "katalóg pre 64-bit");
        Expect.Equal(("07/18/2016", "11.0.0.1166"), inf.DriverVer, "dátum a verzia bez komentára");
        Expect.Sequence(["PCI\\VEN_8086&DEV_1C3A", "PCI\\VEN_8086&DEV_1E3A", "PCI\\VEN_8086&DEV_3B64"], inf.HardwareIds(), "len časti pre 64-bit Windows, aj pokračovanie riadku a úvodzovky");
        Expect.Equal("Intel(R) Management Engine Interface; 100%", inf.Value("Strings", "HECI_DESC"), "bodkočiarka v úvodzovkách nie je komentár, %% je %");

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Inf)).ToArray();
        Expect.Equal("Intel", InfFile.Load(utf16).Provider, "INF v UTF-16");
        Expect.Equal(0, InfFile.Parse("[Version]\nClass=Net\n").HardwareIds().Count, "INF bez [Manufacturer]");
    }

    [Check]
    public static void BackupIsCheckedFileByFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "polaris-ovladace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var package = Path.Combine(root, "001-heci");
            Directory.CreateDirectory(Path.Combine(package, "x64"));
            File.WriteAllText(Path.Combine(package, "heci.inf"), Inf);
            File.WriteAllText(Path.Combine(package, "heci.cat"), "katalóg");
            File.WriteAllText(Path.Combine(package, "x64", "heci.sys"), "ovládač");
            var entry = new DriverPackageEntry
            {
                Folder = "001-heci",
                Inf = "heci.inf",
                Catalog = "heci.cat",
                Class = "System",
                Provider = "Intel",
                Version = "11.0.0.1166",
                SupportedIds = [.. InfFile.Parse(Inf).HardwareIds()],
                Devices = [new DriverDevice("Intel(R) Management Engine Interface", ["PCI\\VEN_8086&DEV_1E3A&SUBSYS_00000000", "PCI\\VEN_8086&DEV_1E3A"])],
            };
            foreach (var file in new[] { "heci.inf", "heci.cat", "x64/heci.sys" })
            {
                var path = Path.Combine(package, file);
                entry.Files.Add(new DriverFile(file, RecipeText.FileFingerprint(path), new FileInfo(path).Length));
            }

            var backup = new DriverBackup { Name = "SKUSKA", CreatedUtc = DateTimeOffset.Parse("2026-11-17T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), Windows = "10.0.26200", Packages = [entry] };
            File.WriteAllText(Path.Combine(root, DriverBackup.FileName), DriverBackupFormat.ToJson(backup));

            var read = DriverBackupFormat.Load(root);
            Expect.Equal(DriverBackupFormat.Fingerprint(backup), DriverBackupFormat.Fingerprint(read), "zapísaná a prečítaná záloha je tá istá");
            Expect.Equal(0, DriverBackupFormat.VerifyFiles(root, read).Count, "všetky súbory sedia");

            Expect.True(DriverBackupFormat.Serves(read.Packages[0], ["PCI\\VEN_8086&DEV_1E3A&SUBSYS_05771028", "pci\\ven_8086&dev_1e3a"]), "PC s týmto zariadením balík potrebuje");
            Expect.True(!DriverBackupFormat.Serves(read.Packages[0], ["PCI\\VEN_8086&DEV_9999"]), "zariadenie len pre 32-bit Windows nie");

            File.AppendAllText(Path.Combine(package, "x64", "heci.sys"), "!");
            Expect.True(DriverBackupFormat.VerifyFiles(root, read).Any(p => p.Contains("zmenený", StringComparison.Ordinal)), "zmenený súbor");
            File.Delete(Path.Combine(package, "heci.cat"));
            Expect.True(DriverBackupFormat.VerifyFiles(root, read).Any(p => p.Contains("chýba", StringComparison.Ordinal)), "chýbajúci súbor");

            read.Packages[0].Files.Add(new DriverFile("../mimo.sys", new string('0', 64), 1));
            Expect.True(DriverBackupFormat.Problems(read).Any(p => p.Contains("mimo priečinka", StringComparison.Ordinal)), "cesta von zo zálohy");
            read.Packages[0].Folder = "..\\iny";
            Expect.True(DriverBackupFormat.Problems(read).Count > 0, "zlý priečinok balíka");
            Expect.Throws<InvalidDataException>(() => DriverBackupFormat.FromJson("""{ "schema": 1, "name": "x", "packages": [ { "folder": "001-a", "inf": "a.inf", "file": [] } ] }"""), "neznáme pole");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Check]
    public static void FolderNamesArePlain()
    {
        Expect.Equal("003-heci", DriverBackupFormat.FolderName(3, "heci.inf"), "jednoduché meno");
        Expect.Equal("012-rt640x64", DriverBackupFormat.FolderName(12, "rt640x64.inf"), "číslice");
        Expect.Equal("001-n-ovl-d", DriverBackupFormat.FolderName(1, "Náš Ovl_ád.inf"), "len malé písmená a pomlčky, nikdy dve za sebou");
        Expect.Equal("002-ovladac", DriverBackupFormat.FolderName(2, "ťť.inf"), "meno bez písmen");
    }

    [Check]
    public static void BuildIsNamedFromFirmwareAndWindows()
    {
        // invented makers and models: the names of real PCs of a school never go into the repository
        Expect.Equal("Dell Skusobny 3000", DriverBuilds.Model(new HardwareInfo("Dell Inc.", "Skusobny 3000")), "známy výrobca skrátene");
        Expect.Equal("HP Vzorovy Desk 400", DriverBuilds.Model(new HardwareInfo("Hewlett-Packard", "HP Vzorovy Desk 400")), "výrobca sa neopakuje");
        Expect.Equal("Lenovo Vzorovy Rad 7", DriverBuilds.Model(new HardwareInfo("LENOVO", "1234AB5", "Vzorovy Rad 7")), "Lenovo: model vo verzii, nie typ stroja");
        Expect.Equal("Lenovo 1234AB5", DriverBuilds.Model(new HardwareInfo("LENOVO", "1234AB5", "1.0")), "Lenovo bez modelu vo verzii");
        Expect.Equal("ASUS DOSKA-Z1", DriverBuilds.Model(new HardwareInfo("System manufacturer", "System Product Name", "System Version", "ASUSTeK COMPUTER INC.", "DOSKA-Z1")), "PC zo súčiastok: podľa dosky");
        Expect.Equal("Vyrobca Skusobny 5", DriverBuilds.Model(new HardwareInfo("Vyrobca Computer Co., Ltd.", "Skusobny 5")), "neznámy výrobca bez prípon firmy");
        Expect.Equal<string?>(null, DriverBuilds.Model(new HardwareInfo("To be filled by O.E.M.", "To be filled by O.E.M.", "", "Default string", "Default string")), "nič o modeli");

        Expect.Equal("Windows 11 Pro", DriverBuilds.Edition("Windows 10 Pro", 26200), "Windows 11 má v mene produktu 10");
        Expect.Equal("Windows 10 Pro", DriverBuilds.Edition("Windows 10 Pro", 19045), "Windows 10");
        Expect.Equal("Windows 11 Education", DriverBuilds.Edition("Windows 11 Education", 26100), "už správne meno");
        Expect.Equal("Dell Skusobny 3000 - Windows 11 Pro", DriverBuilds.Name("Dell Skusobny 3000", "Windows 11 Pro"), "meno zostavy");
        Expect.Equal("Neznámy model - Windows 11 Pro", DriverBuilds.Name(null, "Windows 11 Pro"), "bez modelu");
        Expect.Equal("A B C. - Windows 11 Pro", DriverBuilds.FolderName("A/B:C.  - Windows 11 Pro. "), "znaky, ktoré Windows v mene nedovolí");
    }

    [Check]
    public static void NewBackupNeverOverwritesAnOlderOne()
    {
        var root = Path.Combine("E:", "Ovladace");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var day = new DateTime(2026, 11, 19);
        var first = DriverBuilds.NewFolder(root, "Dell Skusobny 3000 - Windows 11 Pro", day, taken.Contains);
        Expect.Equal(Path.Combine(root, "Dell Skusobny 3000 - Windows 11 Pro"), first, "prvá záloha zostavy");
        taken.Add(first);
        var second = DriverBuilds.NewFolder(root, "Dell Skusobny 3000 - Windows 11 Pro", day, taken.Contains);
        Expect.Equal(first + " (2026-11-19)", second, "ďalšia s dňom");
        taken.Add(second);
        Expect.Equal(first + " (2026-11-19, 2)", DriverBuilds.NewFolder(root, "Dell Skusobny 3000 - Windows 11 Pro", day, taken.Contains), "v ten istý deň s číslom");
    }

    [Check]
    public static void BackupOfTheSameBuildIsFound()
    {
        static FoundBackup At(string folder, string hardware, string edition, int day) =>
            new(Path.Combine("E:", folder), new DriverBackup { Name = folder, Hardware = hardware, Edition = edition, CreatedUtc = new DateTimeOffset(2026, 11, day, 8, 0, 0, TimeSpan.Zero) });

        var found = new List<FoundBackup>
        {
            At("Dell Skusobny 3000 - Windows 10 Pro", "Dell Skusobny 3000", "Windows 10 Pro", 20),
            At("Dell Skusobny 3000 - Windows 11 Pro", "Dell Skusobny 3000", "Windows 11 Pro", 1),
            At("Dell Skusobny 3000 - Windows 11 Pro (2026-11-10)", "Dell Skusobny 3000", "Windows 11 Pro", 10),
            At("HP Vzorovy Desk 400 - Windows 11 Pro", "HP Vzorovy Desk 400", "Windows 11 Pro", 25),
            new(Path.Combine("E:", "Dell Skusobny 3000 - Windows 11 Pro (2026-11-15)"), new DriverBackup { Name = "staršia bez modelu", CreatedUtc = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero) }),
        };
        Expect.Equal("Dell Skusobny 3000 - Windows 11 Pro (2026-11-15)", Path.GetFileName(DriverBuilds.Choose(found, "Dell Skusobny 3000", "Windows 11 Pro")?.Folder), "rovnaký model a Windows, najnovšia (aj záloha bez zapísaného modelu podľa mena priečinka)");
        Expect.Equal("Dell Skusobny 3000 - Windows 10 Pro", Path.GetFileName(DriverBuilds.Choose(found, "Dell Skusobny 3000", "Windows 10 Pro")?.Folder), "iné Windows: jeho záloha");
        Expect.Equal("HP Vzorovy Desk 400 - Windows 11 Pro", Path.GetFileName(DriverBuilds.Choose(found, "HP Vzorovy Desk 400", "Windows 11 Education")?.Folder), "rovnaký model, iné vydanie Windows");
        Expect.Equal<FoundBackup?>(null, DriverBuilds.Choose(found, "Acer Iny 1", "Windows 11 Pro"), "iný model nič");

        var backup = new DriverBackup { Name = "Dell Skusobny 3000 - Windows 11 Pro", Hardware = "Dell Skusobny 3000", Edition = "Windows 11 Pro", CreatedUtc = DateTimeOffset.UnixEpoch };
        var again = DriverBackupFormat.FromJson(DriverBackupFormat.ToJson(backup));
        Expect.Equal(("Dell Skusobny 3000", "Windows 11 Pro"), (again.Hardware, again.Edition), "model a Windows v ovladace.json");
    }
}
