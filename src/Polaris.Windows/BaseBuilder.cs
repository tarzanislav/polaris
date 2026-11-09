using Polaris.Core;

namespace Polaris.Windows;

public sealed record BaseBuildResult(Guid PartitionId, long PartitionLength, long UsedBytes, CopyResult Copy, long FileBytes, int BytesPerSector);

/// <summary>Builds the base layer: a still picture (shadow copy) of a running NTFS volume, its used clusters copied
/// into a new dynamic VHDX with one GPT partition of the same size, checked by reading back.</summary>
public static class BaseBuilder
{
    private const uint BlockSize = 2 << 20;
    private const long Mib = 1 << 20;

    public static BaseBuildResult Build(
        string sourceVolume,
        string targetVhdx,
        Action<string> log,
        Action<string, long, long>? progress,
        CancellationToken cancel)
    {
        long partitionLength;
        using (var live = Disks.OpenVolume(@"\\.\" + sourceVolume.TrimEnd('\\'), 0))
        {
            partitionLength = Disks.GetPartition(live).Length;
        }

        log("Vytváram tieňovú kópiu " + sourceVolume + " (snímku bežiaceho systému)…");
        using var shadow = ShadowCopy.Take(sourceVolume, log);
        using var source = Disks.OpenVolume(shadow.DevicePath, Kernel32.GenericRead);
        var ntfs = Volumes.Ntfs(source);
        log($"NTFS: klaster {ntfs.BytesPerCluster} B, sektor {ntfs.BytesPerSector} B, použité {Format.Gb(ntfs.UsedBytes)}.");
        var boot = VolumeCopier.ReadBootSector(source);
        if (!boot.IsNtfs)
        {
            throw new InvalidOperationException(boot.IsBitLocker
                ? "Zväzok je šifrovaný BitLockerom – najprv ho úplne dešifruj."
                : "Zväzok nemá súborový systém NTFS.");
        }

        var (runs, used) = Volumes.UsedRuns(source, ntfs.TotalClusters);
        log($"Obsadené klastre: {used:N0} v {runs.Count:N0} úsekoch.");

        var size = (ulong)((partitionLength + 3 * Mib + Mib - 1) / Mib * Mib);
        log("Vytváram základnú vrstvu " + targetVhdx + "…");
        VirtualDisks.CreateDynamic(targetVhdx, size, (uint)ntfs.BytesPerSector, BlockSize);
        using var disk = VirtualDisks.Attach(targetVhdx, readOnly: false);
        var (offset, partitionId) = Disks.CreateSingleGptPartition(disk.DiskNumber, partitionLength, "POLARIS zaklad");
        var volume = Volumes.WaitForVolumeOnDisk(disk.DiskNumber, TimeSpan.FromSeconds(60));
        using var target = Disks.OpenVolume(volume, Kernel32.GenericRead | Kernel32.GenericWrite);
        Volumes.LockForRawWrite(target);
        var copy = VolumeCopier.Copy(source, target, runs, ntfs.BytesPerCluster, (d, t) => progress?.Invoke("kopírovanie", d, t), cancel);
        log($"Skopírované {Format.Gb(copy.CopiedBytes)} (zapísané {Format.Gb(copy.WrittenBytes)}, prázdne bloky vynechané) za {Format.Duration(copy.Time)}.");
        log("Overujem kópiu spätným čítaním…");
        var check = VolumeCopier.Fingerprint(target, runs, ntfs.BytesPerCluster, (d, t) => progress?.Invoke("overenie", d, t), cancel);
        if (check != copy.Sha256)
        {
            throw new InvalidOperationException("Kópia sa nezhoduje so snímkou (SHA-256). Prevod sa zastavil.");
        }

        log("Kópia sa zhoduje (SHA-256 " + copy.Sha256[..16] + "…).");
        VolumeCopier.PatchBootSectors(target, partitionLength, ntfs.BytesPerSector, (uint)(offset / ntfs.BytesPerSector));
        Volumes.Unlock(target);
        target.Dispose();
        disk.Dispose();
        var fileBytes = new FileInfo(targetVhdx).Length;
        return new BaseBuildResult(partitionId, partitionLength, ntfs.UsedBytes, copy, fileBytes, ntfs.BytesPerSector);
    }

    /// <summary>Attaches a VHDX (read-write) and mounts its only volume in <paramref name="folder"/>; dispose to detach.</summary>
    public static MountedImage Mount(string vhdx, string folder)
    {
        var disk = VirtualDisks.Attach(vhdx, readOnly: false);
        try
        {
            var volume = Volumes.WaitForVolumeOnDisk(disk.DiskNumber, TimeSpan.FromSeconds(60));
            Volumes.Unmount(folder);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder);
            }

            Volumes.Mount(folder, volume);
            var until = DateTime.UtcNow.AddSeconds(30);
            while (Volumes.FileSystem(folder) != "NTFS")
            {
                if (DateTime.UtcNow > until)
                {
                    throw new TimeoutException("Súborový systém kópie sa nepripojil.");
                }

                Thread.Sleep(500);
            }

            return new MountedImage(disk, folder);
        }
        catch
        {
            disk.Dispose();
            throw;
        }
    }

    public sealed class MountedImage(VirtualDisks.AttachedDisk disk, string folder) : IDisposable
    {
        public string Folder { get; } = folder;

        public void Dispose()
        {
            Volumes.Unmount(Folder);
            disk.Dispose();
            try
            {
                Directory.Delete(Folder);
            }
            catch (IOException)
            {
                // an empty folder left behind does no harm
            }
        }
    }
}
