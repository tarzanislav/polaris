using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Polaris.Core;

namespace Polaris.Windows;

/// <summary>The result of copying a volume: what was read, what was written, and the fingerprint of the content.</summary>
public sealed record CopyResult(long CopiedBytes, long WrittenBytes, string Sha256, TimeSpan Time);

/// <summary>Copies only the used clusters of an NTFS volume (from a shadow copy) onto a raw partition of the same size.
/// Everything inside NTFS – rights, links, streams, compression – travels exactly, because the blocks travel.
/// Blocks full of zeros are not written: an empty block of a dynamic VHDX reads as zeros anyway.</summary>
public static unsafe class VolumeCopier
{
    private const int ChunkBytes = 8 << 20;

    public static CopyResult Copy(
        SafeFileHandle source,
        SafeFileHandle target,
        IReadOnlyList<ClusterRun> runs,
        int bytesPerCluster,
        Action<long, long>? progress,
        CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var total = ClusterRuns.TotalClusters(runs) * bytesPerCluster;
        var buffer = (byte*)NativeMemory.AlignedAlloc(ChunkBytes, 4096);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        long written = 0;
        try
        {
            foreach (var chunk in ClusterRuns.Chunks(runs, ChunkBytes / bytesPerCluster))
            {
                cancel.ThrowIfCancellationRequested();
                var offset = chunk.Start * bytesPerCluster;
                var count = checked((int)(chunk.Length * bytesPerCluster));
                Volumes.Read(source, offset, buffer, count);
                var data = new ReadOnlySpan<byte>(buffer, count);
                hash.AppendData(data);
                if (data.IndexOfAnyExcept((byte)0) >= 0)
                {
                    Volumes.Write(target, offset, buffer, count);
                    written += count;
                }

                done += count;
                progress?.Invoke(done, total);
            }

            Check.Win32(Kernel32.FlushFileBuffers(target), "Dopísanie údajov na disk");
            return new CopyResult(done, written, Convert.ToHexString(hash.GetHashAndReset()), clock.Elapsed);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    /// <summary>Reads the same clusters back from the target and returns their fingerprint (must equal the copy's).</summary>
    public static string Fingerprint(SafeFileHandle device, IReadOnlyList<ClusterRun> runs, int bytesPerCluster, Action<long, long>? progress, CancellationToken cancel)
    {
        var total = ClusterRuns.TotalClusters(runs) * bytesPerCluster;
        var buffer = (byte*)NativeMemory.AlignedAlloc(ChunkBytes, 4096);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        try
        {
            foreach (var chunk in ClusterRuns.Chunks(runs, ChunkBytes / bytesPerCluster))
            {
                cancel.ThrowIfCancellationRequested();
                var count = checked((int)(chunk.Length * bytesPerCluster));
                Volumes.Read(device, chunk.Start * bytesPerCluster, buffer, count);
                hash.AppendData(new ReadOnlySpan<byte>(buffer, count));
                done += count;
                progress?.Invoke(done, total);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    /// <summary>Writes the first sector of the volume with the new number of hidden sectors, and its NTFS copy into the
    /// last sector of the partition.</summary>
    public static void PatchBootSectors(SafeFileHandle target, long partitionLength, int bytesPerSector, uint hiddenSectors)
    {
        var sector = (byte*)NativeMemory.AlignedAlloc((nuint)bytesPerSector, 4096);
        try
        {
            Volumes.Read(target, 0, sector, bytesPerSector);
            var span = new Span<byte>(sector, bytesPerSector);
            var boot = NtfsBootSector.Parse(span);
            if (!boot.IsNtfs || !NtfsBootSector.HasSignature(span))
            {
                throw new InvalidOperationException("Skopírovaný zväzok nezačína sektorom NTFS.");
            }

            NtfsBootSector.SetHiddenSectors(span, hiddenSectors);
            Volumes.Write(target, 0, sector, bytesPerSector);
            Volumes.Write(target, partitionLength - bytesPerSector, sector, bytesPerSector);
            Check.Win32(Kernel32.FlushFileBuffers(target), "Dopísanie úvodného sektora");
        }
        finally
        {
            NativeMemory.AlignedFree(sector);
        }
    }

    /// <summary>Reads the first sector of a volume (4096 bytes – a whole sector on 512-byte and 4K disks alike).</summary>
    public static NtfsBootSector ReadBootSector(SafeFileHandle volume)
    {
        var sector = (byte*)NativeMemory.AlignedAlloc(4096, 4096);
        try
        {
            Volumes.Read(volume, 0, sector, 4096);
            return NtfsBootSector.Parse(new ReadOnlySpan<byte>(sector, 512));
        }
        finally
        {
            NativeMemory.AlignedFree(sector);
        }
    }
}
