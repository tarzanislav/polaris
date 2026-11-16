using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Polaris.Install;

/// <summary>Builds a small, real Windows Installer package by own code (through the database API of msi.dll): a product
/// that writes one registry value. It lets the whole road – store, check, install, recognise, remove – be tested on a
/// real Windows, and later POLARIS can package itself the same way.</summary>
[SupportedOSPlatform("windows")]
public static partial class MsiBuilder
{
    public sealed record Product(string Name, string Version, string Manufacturer, Guid ProductCode, Guid UpgradeCode, string RegistryKey, string ValueName, string Value);

    public static void Build(string path, Product p)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Msi.Check(Msi.MsiOpenDatabase(path, 3 /* MSIDBOPEN_CREATE */, out var db), "Vytvorenie balíka MSI");
        try
        {
            string[] tables =
            [
                "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL LOCALIZABLE PRIMARY KEY `Property`)",
                "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)",
                "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)",
                "CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)",
                "CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)",
                "CREATE TABLE `Registry` (`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Value` LONGCHAR LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)",
                "CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)",
            ];
            foreach (var sql in tables)
            {
                Run(db, sql);
            }

            string G(Guid g) => "{" + g.ToString().ToUpperInvariant() + "}";
            foreach (var (name, value) in new[]
            {
                ("ProductCode", G(p.ProductCode)), ("UpgradeCode", G(p.UpgradeCode)), ("ProductName", p.Name),
                ("ProductVersion", p.Version), ("Manufacturer", p.Manufacturer), ("ProductLanguage", "1033"), ("ALLUSERS", "1"),
            })
            {
                Run(db, $"INSERT INTO `Property` (`Property`, `Value`) VALUES ('{Quote(name)}', '{Quote(value)}')");
            }

            Run(db, "INSERT INTO `Directory` (`Directory`, `DefaultDir`) VALUES ('TARGETDIR', 'SourceDir')");
            // the component's key path is the registry value: Windows Installer knows the product is there by it
            Run(db, $"INSERT INTO `Component` (`Component`, `ComponentId`, `Directory_`, `Attributes`, `KeyPath`) VALUES ('Hodnota', '{G(Guid.NewGuid())}', 'TARGETDIR', 4, 'Hodnota')");
            Run(db, $"INSERT INTO `Registry` (`Registry`, `Root`, `Key`, `Name`, `Value`, `Component_`) VALUES ('Hodnota', 2, '{Quote(p.RegistryKey)}', '{Quote(p.ValueName)}', '{Quote(p.Value)}', 'Hodnota')");
            Run(db, "INSERT INTO `Feature` (`Feature`, `Title`, `Display`, `Level`, `Directory_`, `Attributes`) VALUES ('Hlavna', 'POLARIS', 1, 1, 'TARGETDIR', 0)");
            Run(db, "INSERT INTO `FeatureComponents` (`Feature_`, `Component_`) VALUES ('Hlavna', 'Hodnota')");
            foreach (var (action, sequence) in new[]
            {
                ("CostInitialize", 800), ("FileCost", 900), ("CostFinalize", 1000), ("InstallValidate", 1400),
                ("InstallInitialize", 1500), ("ProcessComponents", 1600), ("UnpublishFeatures", 1800),
                ("RemoveRegistryValues", 2600), ("WriteRegistryValues", 5000), ("RegisterProduct", 6100),
                ("PublishFeatures", 6300), ("PublishProduct", 6400), ("InstallFinalize", 6600),
            })
            {
                Run(db, $"INSERT INTO `InstallExecuteSequence` (`Action`, `Sequence`) VALUES ('{action}', {sequence})");
            }

            Summary(db, p);
            Msi.Check(MsiDatabaseCommit(db), "Uloženie balíka MSI");
        }
        finally
        {
            Msi.MsiCloseHandle(db);
        }
    }

    private static void Summary(uint db, Product p)
    {
        Msi.Check(MsiGetSummaryInformation(db, null, 20, out var info), "Súhrn balíka MSI");
        try
        {
            void Text(uint property, string value) => Msi.Check(MsiSummaryInfoSetProperty(info, property, 30 /* VT_LPSTR */, 0, 0, value), "Súhrn balíka MSI");
            void Number(uint property, int value) => Msi.Check(MsiSummaryInfoSetProperty(info, property, 3 /* VT_I4 */, value, 0, null), "Súhrn balíka MSI");
            Text(2, "Installation Database");
            Text(3, p.Name);
            Text(4, p.Manufacturer);
            Text(7, "Intel;1033");                                  // template: platform and language
            Text(9, "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}"); // package code: new for every build
            Number(14, 200);                                         // needs Windows Installer 2.0
            Number(15, 2);                                           // no files to unpack
            Msi.Check(MsiSummaryInfoPersist(info), "Súhrn balíka MSI");
        }
        finally
        {
            Msi.MsiCloseHandle(info);
        }
    }

    private static void Run(uint db, string sql)
    {
        Msi.Check(Msi.MsiDatabaseOpenView(db, sql, out var view), "Zápis do balíka MSI");
        try
        {
            Msi.Check(Msi.MsiViewExecute(view, 0), "Zápis do balíka MSI: " + sql[..Math.Min(40, sql.Length)]);
        }
        finally
        {
            Msi.MsiCloseHandle(view);
        }
    }

    private static string Quote(string text) => text.Contains('\'', StringComparison.Ordinal)
        ? throw new ArgumentException("Apostrof v hodnote balíka nie je dovolený.", nameof(text))
        : text;

    [LibraryImport("msi.dll")]
    private static partial uint MsiDatabaseCommit(uint database);

    [LibraryImport("msi.dll", EntryPoint = "MsiGetSummaryInformationW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiGetSummaryInformation(uint database, string? path, uint updateCount, out uint info);

    [LibraryImport("msi.dll", EntryPoint = "MsiSummaryInfoSetPropertyW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiSummaryInfoSetProperty(uint info, uint property, uint type, int value, nint time, string? text);

    [LibraryImport("msi.dll")]
    private static partial uint MsiSummaryInfoPersist(uint info);
}
