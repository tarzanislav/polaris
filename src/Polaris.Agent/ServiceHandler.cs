using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Runtime.Versioning;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>A failed system call with its number and a Slovak description of what was being done.</summary>
public sealed class SystemCallException(string what, int code)
    : Exception($"{what} – chyba systému {code} (0x{code:X8}): {Marshal.GetPInvokeErrorMessage(code)}")
{
    public int Code { get; } = code;
}

/// <summary>The service control manager of Windows, called directly.</summary>
[SupportedOSPlatform("windows")]
internal static partial class ServiceManager
{
    public const int ErrorServiceDoesNotExist = 1060;
    public const int ErrorServiceAlreadyRunning = 1056;
    public const int ErrorServiceNotActive = 1062;

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint DeleteAccess = 0x10000;
    private const uint OwnProcess = 0x10;
    private const uint ErrorNormal = 1;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ConfigDelayedAutoStart = 3;
    private const uint ControlStop = 1;

    public const uint AutoStart = 2;
    public const uint DemandStart = 3;
    public const uint Disabled = 4;

    public const uint Stopped = 1;
    public const uint Running = 4;

    public sealed record State(uint StartType, bool Delayed, uint Current);

    public static State Read(string name)
    {
        using var service = Open(name, ServiceQueryConfig | ServiceQueryStatus);
        var start = QueryStartType(service, name);
        var delayed = start == AutoStart && QueryDelayed(service);
        if (!QueryServiceStatus(service, out var status))
        {
            throw new SystemCallException($"Stav služby {name}", Marshal.GetLastPInvokeError());
        }

        return new State(start, delayed, status.CurrentState);
    }

    /// <summary>The services (not drivers, not per-user services) whose key name begins with the prefix.</summary>
    public static List<string> NamesBeginning(string prefix)
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var name in services.GetSubKeyNames().Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            using var key = services.OpenSubKey(name);
            // SERVICE_WIN32_OWN_PROCESS 0x10 or SHARE_PROCESS 0x20, without the user-service bits (0x40, 0x80)
            if (key?.GetValue("Type") is int type && (type & 0x30) != 0 && (type & 0xC0) == 0)
            {
                names.Add(name);
            }
        }

        return [.. names.Order(StringComparer.OrdinalIgnoreCase)];
    }

    public static void SetStart(string name, uint startType, bool delayed)
    {
        using var service = Open(name, ServiceQueryConfig | ServiceChangeConfig);
        if (!ChangeServiceConfig(service, ServiceNoChange, startType, ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
        {
            throw new SystemCallException($"Zmena spúšťania služby {name}", Marshal.GetLastPInvokeError());
        }

        if (startType == AutoStart)
        {
            var info = delayed ? 1 : 0;
            if (!ChangeServiceConfig2(service, ConfigDelayedAutoStart, ref info))
            {
                throw new SystemCallException($"Oneskorené spúšťanie služby {name}", Marshal.GetLastPInvokeError());
            }
        }
    }

    /// <summary>Starts or stops the service and waits until it is there (at most the limit).</summary>
    public static void SetRunning(string name, bool run, TimeSpan limit)
    {
        using var service = Open(name, ServiceQueryStatus | ServiceStart | ServiceStop);
        if (run)
        {
            if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() is var e && e != ErrorServiceAlreadyRunning)
            {
                throw new SystemCallException($"Spustenie služby {name}", e);
            }
        }
        else if (!ControlService(service, ControlStop, out _) && Marshal.GetLastPInvokeError() is var e && e != ErrorServiceNotActive)
        {
            throw new SystemCallException($"Zastavenie služby {name}", e);
        }

        var wanted = run ? Running : Stopped;
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (QueryServiceStatus(service, out var status) && status.CurrentState == wanted)
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"Služba {name} sa do {limit.TotalSeconds:0} s {(run ? "nespustila" : "nezastavila")}.");
    }

    /// <summary>Creates the service, or updates its program and start when it exists; runs as LocalSystem, starts
    /// automatically, is restarted by Windows after a failure.</summary>
    public static void Install(string name, string displayName, string description, string commandLine)
    {
        using var manager = OpenManager(ScManagerConnect | ScManagerCreateService);
        var service = OpenService(manager, name, ServiceAllAccess);
        if (service.IsInvalid)
        {
            var e = Marshal.GetLastPInvokeError();
            service.Dispose();
            if (e != ErrorServiceDoesNotExist)
            {
                throw new SystemCallException($"Otvorenie služby {name}", e);
            }

            service = CreateService(manager, name, displayName, ServiceAllAccess, OwnProcess, AutoStart, ErrorNormal, commandLine, null, IntPtr.Zero, null, null, null);
            if (service.IsInvalid)
            {
                var c = Marshal.GetLastPInvokeError();
                service.Dispose();
                throw new SystemCallException($"Vytvorenie služby {name}", c);
            }
        }
        else if (!ChangeServiceConfig(service, OwnProcess, AutoStart, ErrorNormal, commandLine, null, IntPtr.Zero, null, "LocalSystem", null, displayName))
        {
            var c = Marshal.GetLastPInvokeError();
            service.Dispose();
            throw new SystemCallException($"Zmena služby {name}", c);
        }

        using (service)
        {
            var text = Marshal.StringToHGlobalUni(description);
            var actions = Marshal.AllocHGlobal(3 * 8);
            var failure = Marshal.AllocHGlobal(8 + (3 * IntPtr.Size) + 8);
            try
            {
                var describe = text;
                _ = ChangeServiceConfig2Ptr(service, 1 /* SERVICE_CONFIG_DESCRIPTION */, ref describe);

                // three restarts after 10 s, counters reset after a day
                for (var i = 0; i < 3; i++)
                {
                    Marshal.WriteInt32(actions, i * 8, 1 /* SC_ACTION_RESTART */);
                    Marshal.WriteInt32(actions, (i * 8) + 4, 10_000);
                }

                // SERVICE_FAILURE_ACTIONSW: DWORD reset, LPWSTR reboot, LPWSTR command, DWORD count, SC_ACTION* actions
                var offset = 0;
                Marshal.WriteInt32(failure, offset, 86_400);
                offset += IntPtr.Size;
                Marshal.WriteIntPtr(failure, offset, IntPtr.Zero);
                offset += IntPtr.Size;
                Marshal.WriteIntPtr(failure, offset, IntPtr.Zero);
                offset += IntPtr.Size;
                Marshal.WriteInt32(failure, offset, 3);
                offset += IntPtr.Size;
                Marshal.WriteIntPtr(failure, offset, actions);
                if (!ChangeServiceConfig2Raw(service, 2 /* SERVICE_CONFIG_FAILURE_ACTIONS */, failure))
                {
                    throw new SystemCallException($"Obnova služby {name} po páde", Marshal.GetLastPInvokeError());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(text);
                Marshal.FreeHGlobal(actions);
                Marshal.FreeHGlobal(failure);
            }
        }
    }

    public static bool Exists(string name)
    {
        using var manager = OpenManager(ScManagerConnect);
        using var service = OpenService(manager, name, ServiceQueryStatus);
        return !service.IsInvalid;
    }

    public static void Remove(string name)
    {
        using var service = Open(name, ServiceQueryStatus | ServiceStop | DeleteAccess);
        if (QueryServiceStatus(service, out var status) && status.CurrentState != Stopped)
        {
            _ = ControlService(service, ControlStop, out _);
        }

        if (!DeleteService(service))
        {
            throw new SystemCallException($"Odstránenie služby {name}", Marshal.GetLastPInvokeError());
        }
    }

    private static SafeServiceHandle OpenManager(uint access)
    {
        var manager = OpenSCManager(null, null, access);
        if (manager.IsInvalid)
        {
            var e = Marshal.GetLastPInvokeError();
            manager.Dispose();
            throw new SystemCallException("Otvorenie správcu služieb", e);
        }

        return manager;
    }

    private static uint QueryStartType(SafeServiceHandle service, string name)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
        var buffer = Marshal.AllocHGlobal(Math.Max(needed, 64));
        try
        {
            if (!QueryServiceConfig(service, buffer, Math.Max(needed, 64), out _))
            {
                throw new SystemCallException($"Nastavenie služby {name}", Marshal.GetLastPInvokeError());
            }

            return unchecked((uint)Marshal.ReadInt32(buffer, 4)); // QUERY_SERVICE_CONFIGW.dwStartType
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool QueryDelayed(SafeServiceHandle service)
    {
        var buffer = Marshal.AllocHGlobal(16);
        try
        {
            return QueryServiceConfig2(service, ConfigDelayedAutoStart, buffer, 16, out _) && Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SafeServiceHandle Open(string name, uint access)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            var e = Marshal.GetLastPInvokeError();
            manager.Dispose();
            throw new SystemCallException("Otvorenie správcu služieb", e);
        }

        using (manager)
        {
            var service = OpenService(manager, name, access);
            if (service.IsInvalid)
            {
                var e = Marshal.GetLastPInvokeError();
                service.Dispose();
                throw e == ErrorServiceDoesNotExist
                    ? new ServiceMissingException(name)
                    : new SystemCallException($"Otvorenie služby {name}", e);
            }

            return service;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle OpenService(SafeServiceHandle manager, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig(SafeServiceHandle service, IntPtr buffer, int size, out int needed);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig2(SafeServiceHandle service, uint level, IntPtr buffer, int size, out int needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(SafeServiceHandle service, out ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig(
        SafeServiceHandle service, uint type, uint start, uint errorControl, string? binary, string? group, IntPtr tag,
        string? dependencies, string? account, string? password, string? displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2(SafeServiceHandle service, uint level, ref int info);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle CreateService(
        SafeServiceHandle manager, string name, string displayName, uint access, uint type, uint start, uint errorControl,
        string binary, string? group, IntPtr tag, string? dependencies, string? account, string? password);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(SafeServiceHandle service);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2Ptr(SafeServiceHandle service, uint level, ref IntPtr info);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2Raw(SafeServiceHandle service, uint level, IntPtr info);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(SafeServiceHandle service, int count, IntPtr arguments);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(SafeServiceHandle service, uint control, out ServiceStatus status);
}

[SupportedOSPlatform("windows")]
internal sealed partial class SafeServiceHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeServiceHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => CloseServiceHandle(handle);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}

/// <summary>Windows services: how they start and whether they run. A service that does not exist cannot be created by a
/// recipe – the plan says so. A name ending with "*" covers every service whose name begins so; an element for physical
/// PCs only is skipped on a virtual one. Before the first change of a service its original start is remembered, so it can
/// be put back (<c>polaris-agent sluzby vratit</c>).</summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceHandler : IElementHandler
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private readonly Func<bool> _isVirtual;
    private readonly ServiceMemory _memory;

    public ServiceHandler()
        : this(null, null)
    {
    }

    public ServiceHandler(Func<bool>? isVirtual, ServiceMemory? memory)
    {
        _isVirtual = isVirtual ?? Machine.IsVirtual;
        _memory = memory ?? new ServiceMemory();
    }

    public bool CanHandle(Element element) => element is ServiceElement;

    public Observation Observe(Element element)
    {
        var s = (ServiceElement)element;
        if (s.PhysicalOnly && _isVirtual())
        {
            return new Observation(Observed.Present, "virtuálny počítač – prvok platí len pre fyzické PC");
        }

        var names = Names(s);
        if (names.Count == 0)
        {
            return new Observation(Observed.Present, $"žiadna služba sa nezačína na {s.Prefix} – nie je čo vypínať");
        }

        var different = new List<string>();
        foreach (var name in names)
        {
            if (One(s, name) is { } difference)
            {
                different.Add(names.Count > 1 || s.IsPrefix ? $"{name}: {difference}" : difference);
            }
        }

        return different.Count == 0
            ? new Observation(Observed.Present, names.Count == 1 && !s.IsPrefix ? Describe(ServiceManager.Read(names[0])) : $"{names.Count} služieb v poriadku")
            : new Observation(Observed.Different, string.Join("; ", different));
    }

    public void Apply(Element element)
    {
        var s = (ServiceElement)element;
        if (s.PhysicalOnly && _isVirtual())
        {
            return;
        }

        foreach (var name in Names(s))
        {
            if (One(s, name) is null)
            {
                continue;
            }

            var before = ServiceManager.Read(name);
            _memory.Remember(name, before);
            ServiceManager.SetStart(name, StartType(s.Start), s.Start == ServiceStart.AutomaticDelayed);
            if (s.Running is { } run)
            {
                var current = ServiceManager.Read(name).Current;
                if (run != (current == ServiceManager.Running))
                {
                    ServiceManager.SetRunning(name, run, Limit);
                }
            }
        }
    }

    /// <summary>The services the element covers: its one name, or every service beginning with its prefix.</summary>
    private static List<string> Names(ServiceElement s) => s.IsPrefix ? ServiceManager.NamesBeginning(s.Prefix) : [s.Name.Trim()];

    /// <summary>What differs on one service, or null when it is as wanted.</summary>
    private static string? One(ServiceElement s, string name)
    {
        ServiceManager.State state;
        try
        {
            state = ServiceManager.Read(name);
        }
        catch (ServiceMissingException) when (s.Start == ServiceStart.Disabled && s.Running != true)
        {
            // one recipe for every PC: a service this Windows does not have is as off as can be
            return null;
        }

        var startOk = state.StartType == StartType(s.Start) && (s.Start != ServiceStart.Automatic || !state.Delayed)
            && (s.Start != ServiceStart.AutomaticDelayed || state.Delayed);
        var runOk = s.Running is not { } run || (run ? state.Current == ServiceManager.Running : state.Current == ServiceManager.Stopped);
        return startOk && runOk ? null : $"je {Describe(state)}, má byť {Wanted(s)}";
    }

    public static uint StartType(ServiceStart start) => start switch
    {
        ServiceStart.Automatic or ServiceStart.AutomaticDelayed => ServiceManager.AutoStart,
        ServiceStart.Disabled => ServiceManager.Disabled,
        _ => ServiceManager.DemandStart,
    };

    private static string Describe(ServiceManager.State state)
    {
        var start = state.StartType switch
        {
            ServiceManager.AutoStart => state.Delayed ? "automaticky oneskorene" : "automaticky",
            ServiceManager.DemandStart => "ručne",
            ServiceManager.Disabled => "vypnutá",
            _ => "pri štarte systému",
        };
        var run = state.Current switch
        {
            ServiceManager.Running => "beží",
            ServiceManager.Stopped => "nebeží",
            _ => "mení stav",
        };
        return $"{start}, {run}";
    }

    private static string Wanted(ServiceElement s)
    {
        var start = s.Start switch
        {
            ServiceStart.Automatic => "automaticky",
            ServiceStart.AutomaticDelayed => "automaticky oneskorene",
            ServiceStart.Disabled => "vypnutá",
            _ => "ručne",
        };
        return s.Running switch
        {
            true => start + ", beží",
            false => start + ", nebeží",
            _ => start,
        };
    }
}

/// <summary>The service does not exist on this PC.</summary>
public sealed class ServiceMissingException(string name) : InvalidOperationException($"Služba {name} na tomto PC neexistuje.");

/// <summary>The machine itself.</summary>
[SupportedOSPlatform("windows")]
public static class Machine
{
    private static readonly string[] VirtualMakers = ["vmware", "qemu", "innotek", "virtualbox", "xen", "parallels", "red hat", "bochs", "kvm"];

    /// <summary>A virtual computer, by its firmware's maker and model (a physical PC with Hyper-V on – the Sandbox – still
    /// shows its real maker, unlike the processor's hypervisor flag).</summary>
    public static bool IsVirtual()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var maker = (bios?.GetValue("SystemManufacturer") as string ?? "").ToLowerInvariant();
        var model = (bios?.GetValue("SystemProductName") as string ?? "").ToLowerInvariant();
        return VirtualMakers.Any(v => maker.Contains(v, StringComparison.Ordinal) || model.Contains(v, StringComparison.Ordinal))
            || model.Contains("virtual machine", StringComparison.Ordinal);
    }
}

/// <summary>How each service started before POLARIS first changed it (HKLM\SOFTWARE\POLARIS\Agent\Sluzby), so that it can
/// be put back – one service or all. The first state is kept: later changes never overwrite it.</summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceMemory(string key = ServiceMemory.DefaultKey)
{
    public const string DefaultKey = @"SOFTWARE\POLARIS\Agent\Sluzby";

    internal void Remember(string name, ServiceManager.State state)
    {
        using var memory = Registry.LocalMachine.CreateSubKey(key);
        if (memory.GetValue(name) is null)
        {
            memory.SetValue(name, Text(state), RegistryValueKind.String);
        }
    }

    /// <summary>Service → its original start (Automatic, AutomaticDelayed, Manual, Disabled).</summary>
    public SortedDictionary<string, string> All()
    {
        using var memory = Registry.LocalMachine.OpenSubKey(key);
        var all = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in memory?.GetValueNames() ?? [])
        {
            if (memory!.GetValue(name) is string start)
            {
                all[name] = start;
            }
        }

        return all;
    }

    /// <summary>Puts the service's original start back and forgets it; false when nothing was remembered.</summary>
    public bool Restore(string name)
    {
        if (!All().TryGetValue(name, out var original) || !Enum.TryParse<ServiceStart>(original, out var start))
        {
            return false;
        }

        try
        {
            ServiceManager.SetStart(name, ServiceHandler.StartType(start), start == ServiceStart.AutomaticDelayed);
        }
        catch (ServiceMissingException)
        {
            // gone from Windows meanwhile – nothing to put back
        }

        using var memory = Registry.LocalMachine.CreateSubKey(key);
        memory.DeleteValue(name, throwOnMissingValue: false);
        return true;
    }

    private static string Text(ServiceManager.State state) => state.StartType switch
    {
        ServiceManager.AutoStart => state.Delayed ? nameof(ServiceStart.AutomaticDelayed) : nameof(ServiceStart.Automatic),
        ServiceManager.Disabled => nameof(ServiceStart.Disabled),
        _ => nameof(ServiceStart.Manual),
    };
}
