using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Polaris.Windows;

/// <summary>VHDX files through the virtual disk interface of Windows (virtdisk.dll): the base layer, the difference
/// layers above it, attaching a disk to the system.</summary>
public static unsafe partial class VirtualDisks
{
    private const int DeviceVhdx = 3;
    private static readonly Guid VendorMicrosoft = new("ec984aec-a0f9-47e9-901f-71415a66345b");

    private const int AttachNoDriveLetter = 0x2;
    private const int AttachReadOnly = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageType
    {
        public int DeviceId;
        public Guid VendorId;
    }

    /// <summary>CREATE_VIRTUAL_DISK_PARAMETERS, version 2 (the union is larger in newer versions – the size covers it).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct CreateParameters
    {
        [FieldOffset(0)] public int Version;
        [FieldOffset(8)] public Guid UniqueId;
        [FieldOffset(24)] public ulong MaximumSize;
        [FieldOffset(32)] public uint BlockSizeInBytes;
        [FieldOffset(36)] public uint SectorSizeInBytes;
        [FieldOffset(40)] public uint PhysicalSectorSizeInBytes;
        [FieldOffset(48)] public char* ParentPath;
        [FieldOffset(56)] public char* SourcePath;
        [FieldOffset(64)] public int OpenFlags;
        [FieldOffset(68)] public StorageType ParentType;
        [FieldOffset(88)] public StorageType SourceType;
        [FieldOffset(108)] public Guid ResiliencyGuid;
    }

    /// <summary>OPEN_VIRTUAL_DISK_PARAMETERS, version 2.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct OpenParameters
    {
        [FieldOffset(0)] public int Version;
        [FieldOffset(4)] public int GetInfoOnly;
        [FieldOffset(8)] public int ReadOnly;
        [FieldOffset(12)] public Guid ResiliencyGuid;
    }

    /// <summary>ATTACH_VIRTUAL_DISK_PARAMETERS, version 1.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct AttachParameters
    {
        [FieldOffset(0)] public int Version;
    }

    [LibraryImport("virtdisk.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateVirtualDisk(StorageType* type, string path, int access, nint security, int flags, int providerFlags, CreateParameters* parameters, nint overlapped, out SafeFileHandle handle);

    [LibraryImport("virtdisk.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int OpenVirtualDisk(StorageType* type, string path, int access, int flags, OpenParameters* parameters, out SafeFileHandle handle);

    [LibraryImport("virtdisk.dll")]
    private static partial int AttachVirtualDisk(SafeFileHandle disk, nint security, int flags, int providerFlags, AttachParameters* parameters, nint overlapped);

    [LibraryImport("virtdisk.dll")]
    private static partial int GetVirtualDiskPhysicalPath(SafeFileHandle disk, ref int sizeInBytes, char* path);

    [LibraryImport("virtdisk.dll")]
    private static partial int DetachVirtualDisk(SafeFileHandle disk, int flags, int providerFlags);

    private static StorageType Vhdx() => new() { DeviceId = DeviceVhdx, VendorId = VendorMicrosoft };

    /// <summary>A new dynamic VHDX (only written blocks take space).</summary>
    public static void CreateDynamic(string path, ulong maximumSize, uint logicalSector, uint blockSize)
    {
        var type = Vhdx();
        var p = new CreateParameters
        {
            Version = 2,
            UniqueId = Guid.NewGuid(),
            MaximumSize = maximumSize,
            BlockSizeInBytes = blockSize,
            SectorSizeInBytes = logicalSector,
            PhysicalSectorSizeInBytes = 4096,
        };
        Check.Result(CreateVirtualDisk(&type, path, 0, 0, 0, 0, &p, 0, out var handle), "Vytvorenie VHDX " + path);
        handle.Dispose();
    }

    /// <summary>A new empty difference layer above <paramref name="parentPath"/> – the clean session layer.</summary>
    public static void CreateDifferencing(string path, string parentPath)
    {
        var type = Vhdx();
        fixed (char* parent = parentPath)
        {
            var p = new CreateParameters { Version = 2, UniqueId = Guid.NewGuid(), ParentPath = parent };
            Check.Result(CreateVirtualDisk(&type, path, 0, 0, 0, 0, &p, 0, out var handle), "Vytvorenie rozdielovej vrstvy " + path);
            handle.Dispose();
        }
    }

    /// <summary>Attaches a VHDX as a disk of this system, without drive letters. The disk stays attached while the
    /// returned object lives.</summary>
    public static AttachedDisk Attach(string path, bool readOnly)
    {
        var type = Vhdx();
        var o = new OpenParameters { Version = 2, ReadOnly = readOnly ? 1 : 0 };
        Check.Result(OpenVirtualDisk(&type, path, 0, 0, &o, out var handle), "Otvorenie VHDX " + path);
        try
        {
            var a = new AttachParameters { Version = 1 };
            var flags = AttachNoDriveLetter | (readOnly ? AttachReadOnly : 0);
            Check.Result(AttachVirtualDisk(handle, 0, flags, 0, &a, 0), "Pripojenie VHDX " + path);
            var size = 520;
            var buffer = stackalloc char[260];
            Check.Result(GetVirtualDiskPhysicalPath(handle, ref size, buffer), "Cesta pripojeného VHDX");
            var physical = new string(buffer);
            const string prefix = @"\\.\PhysicalDrive";
            if (!physical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !int.TryParse(physical.AsSpan(prefix.Length), out var number))
            {
                throw new InvalidOperationException("Neočakávaná cesta pripojeného disku: " + physical);
            }

            return new AttachedDisk(handle, physical, number);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public sealed class AttachedDisk(SafeFileHandle handle, string physicalPath, int diskNumber) : IDisposable
    {
        public string PhysicalPath { get; } = physicalPath;

        public int DiskNumber { get; } = diskNumber;

        public void Dispose()
        {
            if (!handle.IsClosed)
            {
                _ = DetachVirtualDisk(handle, 0, 0);
                handle.Dispose();
            }
        }
    }
}
