using System.Runtime.InteropServices;

namespace Polaris.Windows;

/// <summary>A shadow copy (VSS) of one volume: a still picture of the running system that can be read while Windows
/// keeps working. Called through the COM interface IVssBackupComponents by its table of methods – no wrappers.
/// The picture disappears when the object is disposed.</summary>
public sealed unsafe partial class ShadowCopy : IDisposable
{
    // positions in the method table of IVssBackupComponents (vsbackup.h)
    private const int Release = 2;
    private const int InitializeForBackup = 5;
    private const int SetBackupState = 6;
    private const int GatherWriterMetadata = 9;
    private const int FreeWriterMetadata = 12;
    private const int PrepareForBackup = 14;
    private const int BackupComplete = 27;
    private const int SetContext = 35;
    private const int StartSnapshotSet = 36;
    private const int AddToSnapshotSet = 37;
    private const int DoSnapshotSet = 38;
    private const int GetSnapshotProperties = 42;

    // IVssAsync
    private const int AsyncWait = 4;
    private const int AsyncQueryStatus = 5;

    private const int VssBackupCopy = 5;
    private const int VssContextBackup = 0;
    private const int VssAsyncFinished = 0x0004230A;

    private nint _components;
    private bool _metadataGathered;

    /// <summary>The device of the picture: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN.</summary>
    public string DevicePath { get; private set; } = "";

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct SnapshotProperties
    {
        [FieldOffset(40)] public char* DeviceObject;
        [FieldOffset(48)] public char* OriginalVolume;
        [FieldOffset(56)] public char* OriginatingMachine;
        [FieldOffset(64)] public char* ServiceMachine;
        [FieldOffset(72)] public char* ExposedName;
        [FieldOffset(80)] public char* ExposedPath;
    }

    [LibraryImport("vssapi.dll")]
    private static partial int CreateVssBackupComponentsInternal(out nint components);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, int model);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeSecurity(nint descriptor, int authServices, nint services, nint reserved1, int authLevel, int impLevel, nint authList, int capabilities, nint reserved3);

    /// <summary>Takes the picture of a volume ("C:\" or "\\?\Volume{…}\"). Writers of the system (registry, services)
    /// are asked to make their data consistent first. Must run on a thread of the multithreaded apartment.</summary>
    public static ShadowCopy Take(string volume, Action<string>? log = null)
    {
        _ = CoInitializeEx(0, 0); // COINIT_MULTITHREADED; S_FALSE or "changed mode" are fine
        // packet privacy, identify; RPC_E_TOO_LATE when the process already set it – fine too
        _ = CoInitializeSecurity(0, -1, 0, 0, 6, 2, 0, 0, 0);
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var copy = new ShadowCopy();
            try
            {
                copy.Create(volume, log);
                return copy;
            }
            catch (Exception e) when (e is SystemCallException or InvalidOperationException)
            {
                last = e;
                copy.Dispose();
                log?.Invoke($"Tieňová kópia, pokus {attempt}: {e.Message}");
                Thread.Sleep(TimeSpan.FromSeconds(10 * attempt));
            }
        }

        throw new InvalidOperationException("Tieňovú kópiu sa nepodarilo vytvoriť: " + last?.Message, last);
    }

    private void Create(string volume, Action<string>? log)
    {
        Check.Result(CreateVssBackupComponentsInternal(out _components), "Vytvorenie objektu tieňovej kópie");
        Hr(((delegate* unmanaged<nint, nint, int>)Method(InitializeForBackup))(_components, 0), "InitializeForBackup");
        Hr(((delegate* unmanaged<nint, int, int>)Method(SetContext))(_components, VssContextBackup), "SetContext");
        Hr(((delegate* unmanaged<nint, byte, byte, int, byte, int>)Method(SetBackupState))(_components, 0, 0, VssBackupCopy, 0), "SetBackupState");
        log?.Invoke("Tieňová kópia: pýtam sa zapisovačov systému…");
        Await(((delegate* unmanaged<nint, nint*, int>)Method(GatherWriterMetadata)), "GatherWriterMetadata");
        _metadataGathered = true;
        Guid set;
        Hr(((delegate* unmanaged<nint, Guid*, int>)Method(StartSnapshotSet))(_components, &set), "StartSnapshotSet");
        Guid id;
        fixed (char* name = Volumes.EnsureSlash(volume))
        {
            Hr(((delegate* unmanaged<nint, char*, Guid, Guid*, int>)Method(AddToSnapshotSet))(_components, name, Guid.Empty, &id), "AddToSnapshotSet");
        }

        log?.Invoke("Tieňová kópia: príprava…");
        Await(((delegate* unmanaged<nint, nint*, int>)Method(PrepareForBackup)), "PrepareForBackup");
        log?.Invoke("Tieňová kópia: snímka…");
        Await(((delegate* unmanaged<nint, nint*, int>)Method(DoSnapshotSet)), "DoSnapshotSet");
        SnapshotProperties p;
        Hr(((delegate* unmanaged<nint, Guid, SnapshotProperties*, int>)Method(GetSnapshotProperties))(_components, id, &p), "GetSnapshotProperties");
        DevicePath = new string(p.DeviceObject);
        Free(p.DeviceObject);
        Free(p.OriginalVolume);
        Free(p.OriginatingMachine);
        Free(p.ServiceMachine);
        Free(p.ExposedName);
        Free(p.ExposedPath);
        if (!DevicePath.StartsWith(@"\\?\GLOBALROOT\Device\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Neočakávané zariadenie tieňovej kópie: " + DevicePath);
        }
    }

    public void Dispose()
    {
        if (_components == 0)
        {
            return;
        }

        try
        {
            if (DevicePath.Length > 0)
            {
                Await(((delegate* unmanaged<nint, nint*, int>)Method(BackupComplete)), "BackupComplete", ignoreErrors: true);
            }

            if (_metadataGathered)
            {
                _ = ((delegate* unmanaged<nint, int>)Method(FreeWriterMetadata))(_components);
            }
        }
        finally
        {
            _ = ((delegate* unmanaged<nint, uint>)Method(Release))(_components);
            _components = 0;
        }
    }

    private nint Method(int index) => (*(nint**)_components)[index];

    private void Await(delegate* unmanaged<nint, nint*, int> start, string what, bool ignoreErrors = false)
    {
        nint async = 0;
        var hr = start(_components, &async);
        if (hr < 0 || async == 0)
        {
            if (!ignoreErrors)
            {
                Hr(hr, what);
            }

            return;
        }

        try
        {
            var table = *(nint**)async;
            _ = ((delegate* unmanaged<nint, uint, int>)table[AsyncWait])(async, uint.MaxValue);
            int status;
            hr = ((delegate* unmanaged<nint, int*, int*, int>)table[AsyncQueryStatus])(async, &status, null);
            if (!ignoreErrors)
            {
                Hr(hr, what + " (stav)");
                if (status != VssAsyncFinished)
                {
                    throw new SystemCallException("Tieňová kópia: " + what + " neskončila úspešne", status);
                }
            }
        }
        finally
        {
            _ = ((delegate* unmanaged<nint, uint>)(*(nint**)async)[Release])(async);
        }
    }

    private static void Hr(int hr, string what)
    {
        if (hr < 0)
        {
            throw new SystemCallException("Tieňová kópia: " + what, hr);
        }
    }

    private static void Free(char* text)
    {
        if (text != null)
        {
            Marshal.FreeCoTaskMem((nint)text);
        }
    }
}
