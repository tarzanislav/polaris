using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Polaris.Install;

/// <summary>The properties that make an MSI package recognisable on a PC.</summary>
public sealed record MsiFacts(string ProductCode, string UpgradeCode, string ProductName, string ProductVersion, string Manufacturer);

/// <summary>The result of an installation or removal: the code of Windows Installer and what it means.</summary>
public sealed record MsiResult(int Code, string Message)
{
    /// <summary>0, or done with a restart wanted (3010, 1641).</summary>
    public bool Ok => Code is 0 or 3010 or 1641;

    public bool RestartWanted => Code is 3010 or 1641;
}

/// <summary>Windows Installer called directly (msi.dll): no msiexec process, no text to read back – the codes and the
/// state come from the API itself. Everything runs silently; a verbose log of every run is written next to the agent's
/// journal so a failure can be understood.</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class Msi
{
    public const string Common = "ALLUSERS=1 REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable";

    /// <summary>Installs a package for the whole PC, silently. <paramref name="properties"/> are added after the common
    /// ones (e.g. "ADDLOCAL=ALL").</summary>
    public static MsiResult Install(string package, string properties, string? log)
    {
        lock (Gate)
        {
            Prepare(log);
            var code = (int)MsiInstallProduct(package, (Common + " " + properties).Trim());
            return new MsiResult(code, Describe(code));
        }
    }

    /// <summary>Removes an installed product by its product code, silently.</summary>
    public static MsiResult Remove(string productCode, string? log)
    {
        lock (Gate)
        {
            Prepare(log);
            var code = (int)MsiConfigureProductEx(productCode, 0 /* INSTALLLEVEL_DEFAULT */, 2 /* INSTALLSTATE_ABSENT */, "REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable");
            return new MsiResult(code, Describe(code));
        }
    }

    /// <summary>Whether a product is installed for the PC (INSTALLSTATE_DEFAULT), and its version.</summary>
    public static (bool Installed, string? Version) State(string productCode)
    {
        var state = MsiQueryProductState(productCode);
        if (state != 5 /* INSTALLSTATE_DEFAULT */)
        {
            return (false, null);
        }

        return (true, ProductInfo(productCode, "VersionString"));
    }

    /// <summary>Reads the properties of a package file without installing it.</summary>
    public static MsiFacts Read(string package)
    {
        Check(MsiOpenDatabase(package, 0 /* MSIDBOPEN_READONLY */, out var db), "Otvorenie balíka MSI " + package);
        try
        {
            string P(string name) => Property(db, name) ?? "";
            var facts = new MsiFacts(P("ProductCode"), P("UpgradeCode"), P("ProductName"), P("ProductVersion"), P("Manufacturer"));
            return facts.ProductCode.Length == 38
                ? facts
                : throw new InvalidDataException("Balík MSI nemá platný ProductCode – nedá sa podľa neho rozpoznať.");
        }
        finally
        {
            MsiCloseHandle(db);
        }
    }

    private static readonly Lock Gate = new();

    private static void Prepare(string? log)
    {
        _ = MsiSetInternalUI(2 /* INSTALLUILEVEL_NONE */, 0);
        if (log is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            _ = MsiEnableLog(0x1FFF /* INSTALLLOGMODE_VERBOSE and the usual ones */, log, 1 /* INSTALLLOGATTRIBUTES_APPEND */);
        }
    }

    private static string? Property(uint db, string name)
    {
        Check(MsiDatabaseOpenView(db, "SELECT `Value` FROM `Property` WHERE `Property`='" + name + "'", out var view), "Čítanie vlastností MSI");
        try
        {
            Check(MsiViewExecute(view, 0), "Čítanie vlastností MSI");
            if (MsiViewFetch(view, out var record) != 0)
            {
                return null;
            }

            try
            {
                return RecordString(record, 1);
            }
            finally
            {
                MsiCloseHandle(record);
            }
        }
        finally
        {
            MsiCloseHandle(view);
        }
    }

    internal static string RecordString(uint record, uint field) =>
        ReadText((buffer, size) => MsiRecordGetString(record, field, buffer, size)) ?? throw new InvalidOperationException("Hodnota MSI sa nedá prečítať.");

    private static string? ProductInfo(string productCode, string property) =>
        ReadText((buffer, size) => MsiGetProductInfo(productCode, property, buffer, size));

    /// <summary>The pattern of Windows Installer for texts: ask for the size, then read into a buffer big enough.</summary>
    private static string? ReadText(Func<nint, nint, uint> call)
    {
        uint size = 0;
        var status = call(0, (nint)(&size));
        if (status is not (0 or 234 /* ERROR_MORE_DATA */))
        {
            return null;
        }

        var buffer = new char[size + 1];
        size++;
        fixed (char* b = buffer)
        {
            return call((nint)b, (nint)(&size)) == 0 ? new string(b, 0, (int)size) : null;
        }
    }

    internal static void Check(uint status, string what)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"{what}: {Describe((int)status)} ({status}).");
        }
    }

    /// <summary>The meaning of a code, in the language of Windows, with the codes a school sees most often explained.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "hotovo",
        3010 => "hotovo, dokončí sa po reštarte",
        1641 => "hotovo, inštalátor spustil reštart",
        1618 => "práve beží iná inštalácia – skúsi sa znova pri ďalšom behu",
        1603 => "inštalácia zlyhala (podrobnosti v zázname MSI)",
        1605 => "produkt nie je nainštalovaný",
        1638 => "iná verzia tohto produktu je už nainštalovaná",
        1925 => "chýbajú práva správcu",
        _ => Marshal.GetPInvokeErrorMessage(code),
    };

    [LibraryImport("msi.dll", EntryPoint = "MsiInstallProductW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiInstallProduct(string package, string commandLine);

    [LibraryImport("msi.dll", EntryPoint = "MsiConfigureProductExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiConfigureProductEx(string product, int level, int state, string commandLine);

    [LibraryImport("msi.dll", EntryPoint = "MsiQueryProductStateW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MsiQueryProductState(string product);

    [LibraryImport("msi.dll", EntryPoint = "MsiGetProductInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiGetProductInfo(string product, string property, nint value, nint size);

    [LibraryImport("msi.dll")]
    private static partial int MsiSetInternalUI(int level, nint window);

    [LibraryImport("msi.dll", EntryPoint = "MsiEnableLogW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiEnableLog(uint mode, string file, uint attributes);

    [LibraryImport("msi.dll", EntryPoint = "MsiOpenDatabaseW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint MsiOpenDatabase(string path, nint persist, out uint database);

    [LibraryImport("msi.dll", EntryPoint = "MsiDatabaseOpenViewW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint MsiDatabaseOpenView(uint database, string query, out uint view);

    [LibraryImport("msi.dll")]
    internal static partial uint MsiViewExecute(uint view, uint record);

    [LibraryImport("msi.dll")]
    internal static partial uint MsiViewFetch(uint view, out uint record);

    [LibraryImport("msi.dll", EntryPoint = "MsiRecordGetStringW")]
    private static partial uint MsiRecordGetString(uint record, uint field, nint value, nint size);

    [LibraryImport("msi.dll")]
    internal static partial uint MsiCloseHandle(uint handle);
}
