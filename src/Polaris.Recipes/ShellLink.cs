using System.Buffers.Binary;
using System.Text;

namespace Polaris.Recipes;

/// <summary>A Windows shortcut (.lnk) written by own code after the published format (MS-SHLLINK): a header, the target
/// as a local path (LinkInfo, also in UTF-16) and the working folder. No shell, no COM – the Start menu shows it with the
/// target's own icon. Plain bytes, so it is checked on any system.</summary>
public static class ShellLink
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkInfo = 0x02;
    private const uint HasWorkingDir = 0x10;
    private const uint IsUnicode = 0x80;
    private const int LinkInfoHeaderSize = 0x24; // with the offsets of the UTF-16 paths
    private const uint VolumeIdAndLocalBasePath = 0x1;
    private const uint DriveFixed = 3;
    private const uint ShowNormal = 1;

    private static readonly byte[] LinkClsid = new Guid("00021401-0000-0000-c000-000000000046").ToByteArray();

    public static byte[] Create(string target, string? workingFolder = null)
    {
        if (target.Length < 3 || target[1] != ':' || target[2] != '\\')
        {
            throw new ArgumentException("Cieľ odkazu musí byť úplná cesta na disku (napr. C:\\…).", nameof(target));
        }

        workingFolder ??= target[..Math.Max(3, target.LastIndexOf('\\'))];
        var bytes = new List<byte>(512);

        // ShellLinkHeader
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, HeaderSize);
        LinkClsid.CopyTo(header, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), HasLinkInfo | HasWorkingDir | IsUnicode);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 0x20); // FILE_ATTRIBUTE_ARCHIVE
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), ShowNormal);
        bytes.AddRange(header);

        // LinkInfo: VolumeID, then the path in ANSI (plain letters only) and in UTF-16 (the one Windows uses)
        var volume = new byte[0x11];
        BinaryPrimitives.WriteUInt32LittleEndian(volume, (uint)volume.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(4), DriveFixed);
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(12), 0x10); // the label (empty) right after
        var ansiPath = Encoding.ASCII.GetBytes(new string(target.Select(c => c < 0x80 ? c : '_').ToArray()) + "\0");
        var ansiSuffix = new byte[] { 0 };
        var unicodePath = Encoding.Unicode.GetBytes(target + "\0");
        var unicodeSuffix = new byte[] { 0, 0 };

        var volumeOffset = LinkInfoHeaderSize;
        var pathOffset = volumeOffset + volume.Length;
        var suffixOffset = pathOffset + ansiPath.Length;
        var unicodePathOffset = suffixOffset + ansiSuffix.Length;
        unicodePathOffset += unicodePathOffset % 2; // UTF-16 on an even offset
        var unicodeSuffixOffset = unicodePathOffset + unicodePath.Length;
        var linkInfoSize = unicodeSuffixOffset + unicodeSuffix.Length;

        var info = new byte[linkInfoSize];
        BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)linkInfoSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), LinkInfoHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), VolumeIdAndLocalBasePath);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(12), (uint)volumeOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), (uint)pathOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(20), 0); // no network path
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(24), (uint)suffixOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(28), (uint)unicodePathOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(32), (uint)unicodeSuffixOffset);
        volume.CopyTo(info, volumeOffset);
        ansiPath.CopyTo(info, pathOffset);
        ansiSuffix.CopyTo(info, suffixOffset);
        unicodePath.CopyTo(info, unicodePathOffset);
        unicodeSuffix.CopyTo(info, unicodeSuffixOffset);
        bytes.AddRange(info);

        // StringData: the working folder (count of characters, then UTF-16 without a closing zero)
        var count = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(count, (ushort)workingFolder.Length);
        bytes.AddRange(count);
        bytes.AddRange(Encoding.Unicode.GetBytes(workingFolder));

        // ExtraData: only the terminal block
        bytes.AddRange(new byte[4]);
        return [.. bytes];
    }

    /// <summary>The target path of a shortcut this class wrote (the UTF-16 local path of its LinkInfo); null otherwise.</summary>
    public static string? Target(ReadOnlySpan<byte> link)
    {
        if (link.Length < HeaderSize + LinkInfoHeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(link) != HeaderSize
            || !link.Slice(4, 16).SequenceEqual(LinkClsid) || (BinaryPrimitives.ReadUInt32LittleEndian(link[20..]) & HasLinkInfo) == 0)
        {
            return null;
        }

        var info = link[HeaderSize..];
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(info);
        if (size > info.Length || BinaryPrimitives.ReadUInt32LittleEndian(info[4..]) < LinkInfoHeaderSize)
        {
            return null;
        }

        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[28..]);
        var text = MemoryMarshalChars(info[offset..size]);
        var end = text.IndexOf('\0');
        return end < 0 ? null : text[..end];
    }

    private static string MemoryMarshalChars(ReadOnlySpan<byte> utf16) => Encoding.Unicode.GetString(utf16[..(utf16.Length & ~1)]);
}
