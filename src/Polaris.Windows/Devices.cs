using System.Runtime.InteropServices;

namespace Polaris.Windows;

/// <summary>A device of the PC as the Device Manager sees it, with its driver.</summary>
public sealed record DeviceInfo(
    string InstanceId,
    string Name,
    string Class,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    bool HasProblem,
    int Problem,
    string? InfPath,
    string? DriverProvider,
    string? DriverVersion,
    DateTime? DriverDate)
{
    /// <summary>Windows found no driver for it (code 28 in the Device Manager).</summary>
    public bool MissingDriver => HasProblem && Problem == Devices.ProblemNoDriver;
}

/// <summary>The devices present in the PC, read through the configuration manager of Windows (cfgmgr32) – the same
/// source the Device Manager uses.</summary>
public static unsafe partial class Devices
{
    public const int ProblemNoDriver = 28; // CM_PROB_FAILED_INSTALL

    private const uint FilterPresent = 0x100; // CM_GETIDLIST_FILTER_PRESENT
    private const uint HasProblemFlag = 0x400; // DN_HAS_PROBLEM
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const uint TypeString = 0x12;
    private const uint TypeStringList = 0x2012;
    private const uint TypeFileTime = 0x10;

    private static readonly Guid DeviceKeys = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid DriverKeys = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");

    public static List<DeviceInfo> Present()
    {
        uint length;
        int code;
        char[] buffer;
        do
        {
            code = CM_Get_Device_ID_List_SizeW(out length, null, FilterPresent);
            if (code != CrSuccess)
            {
                throw new SystemCallException("Zoznam zariadení", code);
            }

            buffer = new char[length];
            fixed (char* p = buffer)
            {
                code = CM_Get_Device_ID_ListW(null, p, length, FilterPresent);
            }
        }
        while (code == CrBufferSmall); // a device came in between

        if (code != CrSuccess)
        {
            throw new SystemCallException("Zoznam zariadení", code);
        }

        var list = new List<DeviceInfo>();
        foreach (var id in MultiString(buffer))
        {
            if (CM_Locate_DevNodeW(out var node, id, 0) != CrSuccess)
            {
                continue; // gone in the meantime
            }

            var hasProblem = false;
            var problem = 0;
            if (CM_Get_DevNode_Status(out var status, out var number, node, 0) == CrSuccess)
            {
                hasProblem = (status & HasProblemFlag) != 0;
                problem = (int)number;
            }

            var name = Text(node, DeviceKeys, 14) ?? Text(node, DeviceKeys, 2) ?? id; // FriendlyName, DeviceDesc
            list.Add(new DeviceInfo(
                id,
                name,
                Text(node, DeviceKeys, 9) ?? "",       // Class
                List(node, DeviceKeys, 3),              // HardwareIds
                List(node, DeviceKeys, 4),              // CompatibleIds
                hasProblem,
                problem,
                Text(node, DriverKeys, 5),              // DriverInfPath
                Text(node, DriverKeys, 9),              // DriverProvider
                Text(node, DriverKeys, 3),              // DriverVersion
                Time(node, DriverKeys, 2)));            // DriverDate
        }

        return list;
    }

    private static byte[]? Property(uint node, Guid set, uint id, uint wantedType)
    {
        var key = new DevPropKey { Set = set, Id = id };
        uint size = 0;
        var code = CM_Get_DevNode_PropertyW(node, &key, out var type, null, ref size, 0);
        if (code != CrBufferSmall || size == 0 || type != wantedType)
        {
            return null;
        }

        var data = new byte[size];
        fixed (byte* p = data)
        {
            code = CM_Get_DevNode_PropertyW(node, &key, out type, p, ref size, 0);
        }

        return code == CrSuccess && type == wantedType ? data : null;
    }

    private static string? Text(uint node, Guid set, uint id) =>
        Property(node, set, id, TypeString) is { } data ? MultiString(MemoryMarshal.Cast<byte, char>(data).ToArray()).FirstOrDefault() : null;

    private static List<string> List(uint node, Guid set, uint id) =>
        Property(node, set, id, TypeStringList) is { } data ? MultiString(MemoryMarshal.Cast<byte, char>(data).ToArray()) : [];

    private static DateTime? Time(uint node, Guid set, uint id) =>
        Property(node, set, id, TypeFileTime) is { Length: 8 } data ? DateTime.FromFileTimeUtc(BitConverter.ToInt64(data)) : null;

    private static List<string> MultiString(char[] chars)
    {
        var list = new List<string>();
        var start = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] != '\0')
            {
                continue;
            }

            if (i == start)
            {
                break; // the closing double zero
            }

            list.Add(new string(chars, start, i - start));
            start = i + 1;
        }

        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Set;
        public uint Id;
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_ListW(string? filter, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint node, string id, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_PropertyW(uint node, DevPropKey* key, out uint type, byte* buffer, ref uint size, uint flags);
}

/// <summary>The result of installing a package: Applied = some device took it; Reboot = Windows needs a restart.</summary>
public readonly record struct DriverInstall(bool Applied, bool Reboot);

/// <summary>The driver store of Windows: the third-party packages in it and installing a package into it (and onto the
/// devices it fits better than their present driver).</summary>
public static unsafe partial class DriverStore
{
    /// <summary>The published names of third-party packages (oem0.inf, oem1.inf …).</summary>
    public static List<string> ThirdParty()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "oem*.inf").Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }

    /// <summary>The INF of a published package in the driver store (its folder holds the whole package).</summary>
    public static string Location(string publishedInf)
    {
        var buffer = new char[1024];
        fixed (char* p = buffer)
        {
            if (!SetupGetInfDriverStoreLocationW(publishedInf, 0, null, p, (uint)buffer.Length, out var needed))
            {
                throw new SystemCallException($"Umiestnenie ovládača {publishedInf}", Marshal.GetLastPInvokeError());
            }

            return new string(p, 0, (int)Math.Max(0, needed - 1));
        }
    }

    /// <summary>Puts the package into the driver store and onto the devices it fits better than their present driver.
    /// Windows itself refuses a package without a valid signature.</summary>
    public static DriverInstall Install(string inf)
    {
        if (DiInstallDriverW(0, Path.GetFullPath(inf), 0, out var reboot))
        {
            return new DriverInstall(Applied: true, reboot);
        }

        var error = Marshal.GetLastPInvokeError();
        return error == NoMoreItems
            ? new DriverInstall(Applied: false, Reboot: false) // in the store; no device takes it (theirs is as good or better)
            : throw new SystemCallException($"Inštalácia ovládača {Path.GetFileName(inf)}", error);
    }

    private const int NoMoreItems = 259; // ERROR_NO_MORE_ITEMS

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupGetInfDriverStoreLocationW(string fileName, nint platform, string? locale, char* buffer, uint size, out uint needed);

    [LibraryImport("newdev.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiInstallDriverW(nint parent, string inf, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
}
