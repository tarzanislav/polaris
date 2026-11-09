using System.Buffers.Binary;
using System.Text;

namespace Polaris.Core;

/// <summary>The few fields of the first sector of an NTFS volume that POLARIS needs.</summary>
public sealed record NtfsBootSector(string OemId, int BytesPerSector, long SectorsPerCluster, uint HiddenSectors, long TotalSectors)
{
    public const string NtfsOem = "NTFS    ";

    /// <summary>A volume encrypted (or prepared for encryption) by BitLocker carries this mark instead of NTFS.</summary>
    public const string BitLockerOem = "-FVE-FS-";

    public bool IsNtfs => OemId == NtfsOem;

    public bool IsBitLocker => OemId == BitLockerOem;

    public long BytesPerCluster => BytesPerSector * SectorsPerCluster;

    public static NtfsBootSector Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512)
        {
            throw new ArgumentException("Úvodný sektor má aspoň 512 bajtov.");
        }

        var oem = Encoding.ASCII.GetString(sector.Slice(3, 8));
        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[0x0B..]);
        var raw = sector[0x0D];
        // above 0x80 the field holds a negative power of two: 0xF4 means 2^12 sectors per cluster
        long sectorsPerCluster = raw <= 0x80 ? raw : 1L << (256 - raw);
        var hidden = BinaryPrimitives.ReadUInt32LittleEndian(sector[0x1C..]);
        var total = BinaryPrimitives.ReadInt64LittleEndian(sector[0x28..]);
        return new NtfsBootSector(oem, bytesPerSector, sectorsPerCluster, hidden, total);
    }

    /// <summary>The number of sectors before the partition – it changes when the volume moves to another disk.</summary>
    public static void SetHiddenSectors(Span<byte> sector, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(sector[0x1C..], value);

    public static bool HasSignature(ReadOnlySpan<byte> sector) => sector[510] == 0x55 && sector[511] == 0xAA;
}
