using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Polaris.Core;

namespace Polaris.Windows;

/// <summary>NTFS facts of a volume (FSCTL_GET_NTFS_VOLUME_DATA).</summary>
public sealed record NtfsVolumeData(long NumberSectors, long TotalClusters, long FreeClusters, int BytesPerSector, int BytesPerCluster)
{
    public long UsedBytes => (TotalClusters - FreeClusters) * BytesPerCluster;
}

/// <summary>Volumes: names, mount points, the allocation bitmap, locking for raw writing.</summary>
public static unsafe class Volumes
{
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlUnlockVolume = 0x0009001C;
    private const uint FsctlDismountVolume = 0x00090020;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    private const uint FsctlGetVolumeBitmap = 0x0009006F;
    private const uint FsctlAllowExtendedDasdIo = 0x00090083;
    private const int ErrorMoreData = 234;

    /// <summary>All volume names of this system (\\?\Volume{…}\).</summary>
    public static List<string> All()
    {
        var list = new List<string>();
        var buffer = stackalloc char[64];
        var find = Kernel32.FindFirstVolume(buffer, 64);
        if (find == -1)
        {
            return list;
        }

        try
        {
            do
            {
                list.Add(new string(buffer));
            }
            while (Kernel32.FindNextVolume(find, buffer, 64));
        }
        finally
        {
            Kernel32.FindVolumeClose(find);
        }

        return list;
    }

    public static List<string> PathsOf(string volume)
    {
        var buffer = stackalloc char[2048];
        if (!Kernel32.GetVolumePathNamesForVolumeName(volume, buffer, 2048, out _))
        {
            return [];
        }

        return new string(buffer, 0, 2048).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>The volume name of a drive or mount point ("C:\" → "\\?\Volume{…}\").</summary>
    public static string NameOf(string mountPoint)
    {
        var buffer = stackalloc char[64];
        Check.Win32(Kernel32.GetVolumeNameForVolumeMountPoint(EnsureSlash(mountPoint), buffer, 64), "Názov zväzku " + mountPoint);
        return new string(buffer);
    }

    public static string FileSystem(string root)
    {
        var fs = stackalloc char[32];
        return Kernel32.GetVolumeInformation(EnsureSlash(root), null, 0, out _, out _, out _, fs, 32) ? new string(fs) : "";
    }

    public static (long Free, long Total) Space(string directory)
    {
        Check.Win32(Kernel32.GetDiskFreeSpaceEx(EnsureSlash(directory), out var free, out var total, out _), "Voľné miesto " + directory);
        return ((long)free, (long)total);
    }

    /// <summary>Waits until the attached disk shows its volume and returns its name.</summary>
    public static string WaitForVolumeOnDisk(int diskNumber, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            foreach (var volume in All())
            {
                try
                {
                    using var handle = Disks.OpenVolume(volume, 0);
                    if (Disks.Extents(handle).Any(e => e.Disk == diskNumber))
                    {
                        return volume;
                    }
                }
                catch (SystemCallException)
                {
                    // a volume that just disappeared or cannot be asked – not ours
                }
            }

            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException($"Zväzok na disku {diskNumber} sa neobjavil.");
            }

            Thread.Sleep(500);
        }
    }

    public static void Mount(string folder, string volume)
    {
        Directory.CreateDirectory(folder);
        Check.Win32(Kernel32.SetVolumeMountPoint(EnsureSlash(folder), EnsureSlash(volume)), "Pripojenie zväzku do priečinka " + folder);
    }

    public static void Unmount(string folder)
    {
        if (Directory.Exists(folder))
        {
            _ = Kernel32.DeleteVolumeMountPoint(EnsureSlash(folder));
        }
    }

    public static NtfsVolumeData Ntfs(SafeFileHandle volume)
    {
        var b = stackalloc byte[128];
        Check.Win32(Kernel32.DeviceIoControl(volume, FsctlGetNtfsVolumeData, null, 0, b, 128, out _, 0), "Údaje NTFS");
        return new NtfsVolumeData(*(long*)(b + 8), *(long*)(b + 16), *(long*)(b + 24), *(int*)(b + 40), *(int*)(b + 44));
    }

    /// <summary>Reads the whole allocation bitmap of a volume into runs of used clusters.</summary>
    public static (IReadOnlyList<ClusterRun> Runs, long UsedClusters) UsedRuns(SafeFileHandle volume, long totalClusters)
    {
        const int size = 1 << 20;
        var buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            var builder = new ClusterRunBuilder();
            long lcn = 0;
            while (lcn < totalClusters)
            {
                long start = lcn;
                var ok = Kernel32.DeviceIoControl(volume, FsctlGetVolumeBitmap, &start, 8, buffer, size, out _, 0);
                var error = ok ? 0 : Marshal.GetLastPInvokeError();
                if (!ok && error != ErrorMoreData)
                {
                    throw new SystemCallException("Bitmapa obsadenia zväzku", error);
                }

                var returnedStart = *(long*)buffer;
                var bitmapSize = *(long*)(buffer + 8);
                if (returnedStart != lcn)
                {
                    throw new InvalidOperationException($"Bitmapa začína klastrom {returnedStart}, čakal som {lcn}.");
                }

                var available = (long)(size - 16) * 8;
                var bits = Math.Min(Math.Min(bitmapSize, available), totalClusters - lcn);
                if (bits <= 0)
                {
                    break;
                }

                builder.Add(new ReadOnlySpan<byte>(buffer + 16, (int)((bits + 7) / 8)), lcn, bits);
                lcn += bits;
                if (ok)
                {
                    break;
                }
            }

            return (builder.Finish(), builder.UsedClusters);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>Locks and dismounts a volume so its sectors can be written directly.</summary>
    public static void LockForRawWrite(SafeFileHandle volume)
    {
        var tries = 0;
        while (!Kernel32.DeviceIoControl(volume, FsctlLockVolume, null, 0, null, 0, out _, 0))
        {
            if (++tries >= 20)
            {
                Check.Win32(false, "Uzamknutie zväzku");
            }

            Thread.Sleep(500);
        }

        Check.Win32(Kernel32.DeviceIoControl(volume, FsctlDismountVolume, null, 0, null, 0, out _, 0), "Odpojenie súborového systému");
        AllowWholeVolume(volume);
    }

    /// <summary>Lets reads and writes reach the very last sector (the NTFS copy of the first sector lies there).</summary>
    public static void AllowWholeVolume(SafeFileHandle volume) =>
        _ = Kernel32.DeviceIoControl(volume, FsctlAllowExtendedDasdIo, null, 0, null, 0, out _, 0);

    public static void Unlock(SafeFileHandle volume) =>
        _ = Kernel32.DeviceIoControl(volume, FsctlUnlockVolume, null, 0, null, 0, out _, 0);

    public static void Read(SafeFileHandle device, long offset, byte* buffer, int count)
    {
        var o = new Kernel32.Overlapped { Offset = (uint)offset, OffsetHigh = (uint)(offset >> 32) };
        Check.Win32(Kernel32.ReadFile(device, buffer, count, out var read, &o), $"Čítanie na pozícii {offset}");
        if (read != count)
        {
            throw new IOException($"Prečítaných {read} z {count} bajtov na pozícii {offset}.");
        }
    }

    public static void Write(SafeFileHandle device, long offset, byte* buffer, int count)
    {
        var o = new Kernel32.Overlapped { Offset = (uint)offset, OffsetHigh = (uint)(offset >> 32) };
        Check.Win32(Kernel32.WriteFile(device, buffer, count, out var written, &o), $"Zápis na pozícii {offset}");
        if (written != count)
        {
            throw new IOException($"Zapísaných {written} z {count} bajtov na pozícii {offset}.");
        }
    }

    public static string EnsureSlash(string path) => path.EndsWith('\\') ? path : path + "\\";
}
