using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Polaris.Recipes;
using Polaris.Windows;

namespace Polaris.Agent;

/// <summary>A profile folder copied into one archive file and back through the backup interface of Windows (BackupRead /
/// BackupWrite – what robocopy /B uses): every file with its security, alternate streams and reparse data, as Windows
/// itself keeps them. The archive is one file of the agent, so a pupil can never reach the copies (a copied file would
/// keep the pupil's rights). Reparse points are never followed.</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class ProfileArchive
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x80;
    private const uint FileWriteAttributes = 0x100;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint CreateAlways = 2;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int AttributeReparsePoint = 0x400;
    private const int AttributeDirectory = 0x10;
    private const int Chunk = 1 << 16;

    /// <summary>Everything under the profile folder (not the folder itself), a folder before its content; the content of
    /// skipped folders (caches) is left out when <paramref name="skipCaches"/>.</summary>
    public static List<ProfileItem> Read(string root, bool skipCaches)
    {
        var items = new List<ProfileItem>();
        Walk(root, "", items, skipCaches);
        return items;
    }

    private static void Walk(string root, string relative, List<ProfileItem> items, bool skipCaches)
    {
        var folder = relative.Length == 0 ? root : Path.Combine(root, relative);
        var found = new FileSystemEnumerable<ProfileItem>(
            folder,
            (ref FileSystemEntry e) =>
            {
                var attributes = (int)e.Attributes;
                var kind = (attributes & AttributeReparsePoint) != 0 ? VaultKind.Link
                    : (attributes & AttributeDirectory) != 0 ? VaultKind.Directory
                    : VaultKind.File;
                return new ProfileItem(e.FileName.ToString(), kind, kind == VaultKind.File ? e.Length : 0, e.LastWriteTimeUtc.ToFileTime(), attributes);
            },
            new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false })
            .OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var item in found)
        {
            var path = relative.Length == 0 ? item.Path : relative + "\\" + item.Path;
            items.Add(item with { Path = path });
            if (item.Kind == VaultKind.Directory && !(skipCaches && VaultRules.SkipContent(path)))
            {
                Walk(root, path, items, skipCaches);
            }
        }
    }

    /// <summary>Writes every item into the archive; returns the entries with their place and SHA-256.</summary>
    public static List<VaultEntry> Save(string root, IReadOnlyList<ProfileItem> items, Stream archive)
    {
        Privileges.Enable(Privileges.Backup);
        var entries = new List<VaultEntry>(items.Count);
        var buffer = new byte[Chunk];
        foreach (var item in items)
        {
            var start = archive.Position;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var file = Open(Path.Combine(root, item.Path), GenericRead | ReadControl | FileReadAttributes, OpenExisting, "Čítanie " + item.Path))
            {
                nint context = 0;
                try
                {
                    fixed (byte* b = buffer)
                    {
                        while (true)
                        {
                            Win32(BackupRead(file, b, Chunk, out var read, false, true, ref context), "Záloha " + item.Path);
                            if (read == 0)
                            {
                                break;
                            }

                            archive.Write(buffer, 0, read);
                            sha.AppendData(buffer, 0, read);
                        }
                    }
                }
                finally
                {
                    if (context != 0)
                    {
                        _ = BackupRead(file, null, 0, out _, true, true, ref context);
                    }
                }
            }

            entries.Add(new VaultEntry(item.Path, item.Kind, item.Size, item.WriteTime, item.Attributes, start, archive.Position - start, Convert.ToHexStringLower(sha.GetHashAndReset())));
        }

        return entries;
    }

    /// <summary>Makes the profile as the plan says: removes, then puts back from the archive (each stream checked by its
    /// SHA-256 before it is written), with the times and attributes the thing had.</summary>
    public static void Apply(string root, VaultPlan plan, Stream archive)
    {
        Privileges.Enable(Privileges.Backup);
        Privileges.Enable(Privileges.Restore);
        foreach (var item in plan.Remove)
        {
            Remove(Path.Combine(root, item.Path), item.Kind == VaultKind.Directory);
        }

        foreach (var entry in plan.Put)
        {
            Put(root, entry, archive);
        }
    }

    private static void Put(string root, VaultEntry entry, Stream archive)
    {
        var path = Path.Combine(root, entry.Path);
        var data = new byte[entry.Length];
        archive.Position = entry.Offset;
        archive.ReadExactly(data);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), entry.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Trezor je poškodený: {entry.Path} nesedí s odtlačkom.");
        }

        // a folder (or a junction, which is a folder with reparse data) is made first and gets its stream; a file is
        // written anew; whatever stands in the way goes
        var folder = (entry.Attributes & AttributeDirectory) != 0;
        var raw = GetFileAttributes(@"\\?\" + Path.GetFullPath(path));
        var standing = raw == uint.MaxValue ? (FileAttributes?)null : (FileAttributes)raw;
        if (standing is { } st && (!folder || entry.Kind == VaultKind.Link || (st & FileAttributes.ReparsePoint) != 0 || (st & FileAttributes.Directory) == 0))
        {
            Remove(path, (st & FileAttributes.Directory) != 0 && (st & FileAttributes.ReparsePoint) == 0);
            standing = null;
        }

        if (folder && standing is null)
        {
            Win32(CreateDirectory(@"\\?\" + Path.GetFullPath(path), 0), "Priečinok " + entry.Path);
        }

        var flags = folder ? OpenExisting : CreateAlways;
        using var file = Open(path, GenericWrite | WriteDac | WriteOwner | FileWriteAttributes | ReadControl, flags, "Zápis " + entry.Path);
        nint context = 0;
        try
        {
            fixed (byte* b = data)
            {
                var done = 0;
                while (done < data.Length)
                {
                    var count = Math.Min(Chunk, data.Length - done);
                    Win32(BackupWrite(file, b + done, count, out var written, false, true, ref context), "Obnova " + entry.Path);
                    done += written;
                    if (written == 0)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            if (context != 0)
            {
                _ = BackupWrite(file, null, 0, out _, true, true, ref context);
            }
        }

        // the times and attributes the thing had (written last – a read-only file is still writable before)
        var basic = new FileBasicInfo
        {
            LastWriteTime = entry.WriteTime,
            ChangeTime = entry.WriteTime,
            FileAttributes = Plain(entry.Attributes),
        };
        Win32(SetFileInformationByHandle(file, 0 /* FileBasicInfo */, &basic, sizeof(FileBasicInfo)), "Čas a vlastnosti " + entry.Path);
    }

    /// <summary>Removes a file, a reparse point (not what it points to) or a folder with everything in it, read-only or not.</summary>
    public static void Remove(string path, bool folder)
    {
        if (folder)
        {
            foreach (var e in new FileSystemEnumerable<(string Path, bool Folder)>(
                path,
                (ref FileSystemEntry x) => (x.ToFullPath(), x.IsDirectory && (x.Attributes & FileAttributes.ReparsePoint) == 0),
                new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false }))
            {
                Remove(e.Path, e.Folder);
            }
        }

        using var handle = Open(path, Delete | FileWriteAttributes, OpenExisting, "Mazanie " + path);
        // delete now (POSIX), also when read-only
        var disposition = new FileDispositionInfoEx { Flags = 0x1 | 0x2 | 0x10 };
        Win32(SetFileInformationByHandle(handle, 21 /* FileDispositionInfoEx */, &disposition, sizeof(FileDispositionInfoEx)), "Mazanie " + path);
    }

    /// <summary>The attributes to set (without folder and reparse bits, which come from what the thing is); none = normal.</summary>
    private static uint Plain(int attributes) => (uint)(attributes & ~(AttributeDirectory | AttributeReparsePoint)) is var a && a != 0 ? a : 0x80u;

    private static void Win32(bool ok, string what)
    {
        if (!ok)
        {
            throw new SystemCallException(what, Marshal.GetLastPInvokeError());
        }
    }

    private static SafeFileHandle Open(string path, uint access, uint disposition, string what)
    {
        var handle = CreateFile(@"\\?\" + Path.GetFullPath(path), access, ShareAll, 0, disposition, BackupSemantics | OpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new SystemCallException(what, code);
        }

        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
        public uint Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoEx
    {
        public uint Flags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BackupRead(SafeFileHandle file, byte* buffer, int toRead, out int read, [MarshalAs(UnmanagedType.Bool)] bool abort, [MarshalAs(UnmanagedType.Bool)] bool processSecurity, ref nint context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BackupWrite(SafeFileHandle file, byte* buffer, int toWrite, out int written, [MarshalAs(UnmanagedType.Bool)] bool abort, [MarshalAs(UnmanagedType.Bool)] bool processSecurity, ref nint context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* info, int size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFileAttributes(string path);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectory(string path, nint security);
}
