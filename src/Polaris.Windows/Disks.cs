using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Polaris.Windows;

/// <summary>Partition information of a volume (from the partition table of its disk).</summary>
public sealed record PartitionInfo(bool IsGpt, long StartingOffset, long Length, int PartitionNumber, Guid PartitionId, Guid PartitionType);

/// <summary>Kinds of access when opening a disk or a volume.</summary>
public static class DeviceAccess
{
    public const uint None = 0;
    public const uint Read = 0x80000000;
    public const uint ReadWrite = 0xC0000000;
}

/// <summary>Disks and partitions through the disk control codes of Windows.</summary>
public static unsafe class Disks
{
    private const uint IoctlDiskGetPartitionInfoEx = 0x00070048;
    private const uint IoctlDiskGetDriveLayoutEx = 0x00070050;
    private const uint IoctlDiskSetDriveLayoutEx = 0x0007C054;
    private const uint IoctlDiskCreateDisk = 0x0007C058;
    private const uint IoctlDiskUpdateProperties = 0x00070140;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlVolumeGetDiskExtents = 0x00560000;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    private const int StyleGpt = 1;
    public static readonly Guid BasicDataPartition = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");

    // offsets inside PARTITION_INFORMATION_EX (144 bytes) and DRIVE_LAYOUT_INFORMATION_EX (48-byte header)
    private const int PieStyle = 0;
    private const int PieStart = 8;
    private const int PieLength = 16;
    private const int PieNumber = 24;
    private const int PieRewrite = 28;
    private const int PieGptType = 32;
    private const int PieGptId = 48;
    private const int PieGptAttributes = 64;
    private const int PieGptName = 72;
    private const int PieSize = 144;
    private const int LayoutHeader = 48;

    public static SafeFileHandle OpenDisk(int number, bool write)
    {
        var handle = Kernel32.CreateFile(
            $@"\\.\PhysicalDrive{number}",
            Kernel32.GenericRead | (write ? Kernel32.GenericWrite : 0),
            Kernel32.ShareRead | Kernel32.ShareWrite,
            0,
            Kernel32.OpenExisting,
            0,
            0);
        Check.Win32(!handle.IsInvalid, $"Otvorenie disku {number}");
        return handle;
    }

    /// <summary>Opens a volume as a device (\\?\Volume{…} without the closing backslash, or \\.\C:).</summary>
    public static SafeFileHandle OpenVolume(string devicePath, uint access)
    {
        var handle = Kernel32.CreateFile(devicePath.TrimEnd('\\'), access, Kernel32.ShareRead | Kernel32.ShareWrite, 0, Kernel32.OpenExisting, 0, 0);
        Check.Win32(!handle.IsInvalid, "Otvorenie zväzku " + devicePath);
        return handle;
    }

    public static long Length(SafeFileHandle device)
    {
        long length;
        Check.Win32(Kernel32.DeviceIoControl(device, IoctlDiskGetLengthInfo, null, 0, &length, 8, out _, 0), "Veľkosť zariadenia");
        return length;
    }

    public static PartitionInfo GetPartition(SafeFileHandle volumeOrPartition)
    {
        var buffer = stackalloc byte[PieSize];
        Check.Win32(Kernel32.DeviceIoControl(volumeOrPartition, IoctlDiskGetPartitionInfoEx, null, 0, buffer, PieSize, out _, 0), "Údaje oddielu");
        return ReadPartition(buffer);
    }

    private static PartitionInfo ReadPartition(byte* p)
    {
        var gpt = *(int*)(p + PieStyle) == StyleGpt;
        return new PartitionInfo(
            gpt,
            *(long*)(p + PieStart),
            *(long*)(p + PieLength),
            *(int*)(p + PieNumber),
            gpt ? *(Guid*)(p + PieGptId) : Guid.Empty,
            gpt ? *(Guid*)(p + PieGptType) : Guid.Empty);
    }

    /// <summary>The disk numbers a volume lies on (one for a simple volume).</summary>
    public static IReadOnlyList<(int Disk, long Offset, long Length)> Extents(SafeFileHandle volume)
    {
        const int size = 8 + 24 * 8;
        var buffer = stackalloc byte[size];
        Check.Win32(Kernel32.DeviceIoControl(volume, IoctlVolumeGetDiskExtents, null, 0, buffer, size, out _, 0), "Rozsah zväzku na diskoch");
        var count = *(int*)buffer;
        var list = new List<(int, long, long)>();
        for (var i = 0; i < Math.Min(count, 8); i++)
        {
            var e = buffer + 8 + 24 * i;
            list.Add((*(int*)e, *(long*)(e + 8), *(long*)(e + 16)));
        }

        return list;
    }

    /// <summary>The bus of a disk (SATA, NVMe, a file-backed virtual disk …) – for the report.</summary>
    public static string BusType(SafeFileHandle disk)
    {
        var query = stackalloc byte[12];
        var output = stackalloc byte[1024];
        if (!Kernel32.DeviceIoControl(disk, IoctlStorageQueryProperty, query, 12, output, 1024, out _, 0))
        {
            return "neznáma";
        }

        return *(int*)(output + 28) switch
        {
            1 => "SCSI",
            3 => "ATA",
            7 => "USB",
            8 => "RAID",
            0xA => "SAS",
            0xB => "SATA",
            0xE => "virtuálny",
            0xF => "virtuálny disk zo súboru",
            0x10 => "Storage Spaces",
            0x11 => "NVMe",
            var other => $"typ {other}",
        };
    }

    /// <summary>Is the volume on a disk backed by a file – i.e. is this Windows started from a VHD(X)?</summary>
    public static bool IsFileBacked(SafeFileHandle disk) => BusType(disk) == "virtuálny disk zo súboru";

    /// <summary>Makes an empty attached disk a GPT disk with one basic data partition and returns where it lies.</summary>
    public static (long Offset, Guid PartitionId) CreateSingleGptPartition(int diskNumber, long partitionLength, string name)
    {
        using var disk = OpenDisk(diskNumber, write: true);
        var create = stackalloc byte[24];
        *(int*)create = StyleGpt;
        *(Guid*)(create + 4) = Guid.NewGuid();
        *(int*)(create + 20) = 128;
        Check.Win32(Kernel32.DeviceIoControl(disk, IoctlDiskCreateDisk, create, 24, null, 0, out _, 0), "Vytvorenie tabuľky GPT");
        Check.Win32(Kernel32.DeviceIoControl(disk, IoctlDiskUpdateProperties, null, 0, null, 0, out _, 0), "Obnova údajov disku");

        const int bufferSize = LayoutHeader + 128 * PieSize;
        var layout = (byte*)NativeMemory.AllocZeroed(bufferSize);
        try
        {
            Check.Win32(Kernel32.DeviceIoControl(disk, IoctlDiskGetDriveLayoutEx, null, 0, layout, bufferSize, out _, 0), "Čítanie tabuľky GPT");
            var usableStart = *(long*)(layout + 24);
            var usableLength = *(long*)(layout + 32);
            const long mib = 1024 * 1024;
            var offset = (usableStart + mib - 1) / mib * mib;
            if (offset + partitionLength > usableStart + usableLength)
            {
                throw new InvalidOperationException("Oddiel sa na virtuálny disk nezmestí.");
            }

            var id = Guid.NewGuid();
            *(int*)(layout + 4) = 1; // one partition
            var entry = layout + LayoutHeader;
            new Span<byte>(entry, PieSize).Clear();
            *(int*)(entry + PieStyle) = StyleGpt;
            *(long*)(entry + PieStart) = offset;
            *(long*)(entry + PieLength) = partitionLength;
            *(int*)(entry + PieNumber) = 1;
            entry[PieRewrite] = 1;
            *(Guid*)(entry + PieGptType) = BasicDataPartition;
            *(Guid*)(entry + PieGptId) = id;
            *(ulong*)(entry + PieGptAttributes) = 0;
            var nameChars = (char*)(entry + PieGptName);
            for (var i = 0; i < Math.Min(name.Length, 35); i++)
            {
                nameChars[i] = name[i];
            }

            Check.Win32(Kernel32.DeviceIoControl(disk, IoctlDiskSetDriveLayoutEx, layout, LayoutHeader + PieSize, null, 0, out _, 0), "Zápis oddielu");
            Check.Win32(Kernel32.DeviceIoControl(disk, IoctlDiskUpdateProperties, null, 0, null, 0, out _, 0), "Obnova údajov disku");
            return (offset, id);
        }
        finally
        {
            NativeMemory.Free(layout);
        }
    }
}
