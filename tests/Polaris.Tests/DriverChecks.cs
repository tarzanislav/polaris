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
}
