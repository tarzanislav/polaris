using Microsoft.Win32;

namespace Polaris.Windows;

/// <summary>Privileges of the own process and loading of registry files (hives) from another Windows.</summary>
public static unsafe class Privileges
{
    public const string Backup = "SeBackupPrivilege";
    public const string Restore = "SeRestorePrivilege";
    public const string Shutdown = "SeShutdownPrivilege";

    public static void Enable(string name)
    {
        Check.Win32(Advapi32.OpenProcessToken(-1, Advapi32.TokenAdjustPrivileges | Advapi32.TokenQuery, out var token), "Token procesu");
        try
        {
            Check.Win32(Advapi32.LookupPrivilegeValue(null, name, out var luid), "Oprávnenie " + name);
            var state = new Advapi32.TokenPrivilege { Count = 1, Luid = luid, Attributes = Advapi32.PrivilegeEnabled };
            Check.Win32(Advapi32.AdjustTokenPrivileges(token, false, &state, sizeof(Advapi32.TokenPrivilege), 0, 0), "Zapnutie oprávnenia " + name);
            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (error == 1300) // ERROR_NOT_ALL_ASSIGNED
            {
                throw new SystemCallException("Oprávnenie " + name + " nie je k dispozícii (spusti ako správca)", error);
            }
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }
}

/// <summary>A registry file of another Windows loaded under HKEY_LOCAL_MACHINE\&lt;name&gt;; unloaded on dispose.</summary>
public sealed class LoadedHive : IDisposable
{
    private bool _loaded;

    private LoadedHive(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public static LoadedHive Load(string file, string name)
    {
        Privileges.Enable(Privileges.Backup);
        Privileges.Enable(Privileges.Restore);
        if (Registry.LocalMachine.OpenSubKey(name) is { } stale)
        {
            // left loaded by an interrupted earlier run
            stale.Dispose();
            _ = Advapi32.RegUnLoadKey(Advapi32.LocalMachine, name);
        }

        Check.Result(Advapi32.RegLoadKey(Advapi32.LocalMachine, name, file), "Načítanie registra " + file);
        return new LoadedHive(name) { _loaded = true };
    }

    public RegistryKey Open(string path, bool writable) =>
        Registry.LocalMachine.OpenSubKey(Name + (path.Length > 0 ? "\\" + path : ""), writable)
        ?? throw new InvalidOperationException($"V registri chýba kľúč {path}.");

    public RegistryKey Create(string path) => Registry.LocalMachine.CreateSubKey(Name + "\\" + path, writable: true);

    public void Dispose()
    {
        if (!_loaded)
        {
            return;
        }

        // handles of the key objects may still be closing; a few tries
        for (var i = 0; i < 20; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (Advapi32.RegUnLoadKey(Advapi32.LocalMachine, Name) == 0)
            {
                _loaded = false;
                return;
            }

            Thread.Sleep(250);
        }

        throw new InvalidOperationException("Register " + Name + " sa nepodarilo uvoľniť.");
    }
}
