using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Polaris.Windows;

/// <summary>Runs the program as a Windows service: the dispatcher of the service control manager, the status, the
/// stop request. Own calls of advapi32 (no service library).</summary>
public static unsafe partial class ServiceHost
{
    private const int OwnProcess = 0x10;
    private const int StatePending = 2;
    private const int StateRunning = 4;
    private const int StateStopped = 1;
    private const int AcceptStop = 0x1;
    private const int AcceptShutdown = 0x4;
    private const int ControlStop = 1;
    private const int ControlShutdown = 5;
    private const int ControlInterrogate = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TableEntry
    {
        public char* Name;
        public nint Main;
    }

    private static Action<CancellationToken>? _work;
    private static readonly CancellationTokenSource Stop = new();
    private static nint _statusHandle;
    private static char* _name;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceCtrlDispatcherW(TableEntry* table);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial nint RegisterServiceCtrlHandlerExW(char* name, delegate* unmanaged<int, int, nint, nint, int> handler, nint context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceStatus(nint handle, Status* status);

    /// <summary>Hands the process to the service control manager; <paramref name="work"/> runs on its own thread and
    /// gets the stop request. Returns when the service has stopped. False when not started as a service.</summary>
    public static bool Run(string name, Action<CancellationToken> work)
    {
        _work = work;
        _name = (char*)Marshal.StringToHGlobalUni(name);
        var table = stackalloc TableEntry[2];
        table[0] = new TableEntry { Name = _name, Main = (nint)(delegate* unmanaged<int, char**, void>)&ServiceMain };
        table[1] = default;
        return StartServiceCtrlDispatcherW(table);
    }

    [UnmanagedCallersOnly]
    private static void ServiceMain(int argc, char** argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerExW(_name, &Handler, 0);
        if (_statusHandle == 0)
        {
            return;
        }

        Report(StatePending, 0);
        Report(StateRunning, AcceptStop | AcceptShutdown);
        var exit = 0;
        try
        {
            _work?.Invoke(Stop.Token);
        }
        catch (Exception)
        {
            exit = 1064; // ERROR_EXCEPTION_IN_SERVICE – the manager restarts the service
        }

        Report(StateStopped, 0, exit);
    }

    [UnmanagedCallersOnly]
    private static int Handler(int control, int eventType, nint eventData, nint context)
    {
        if (control is ControlStop or ControlShutdown)
        {
            Report(StatePending + 1, 0); // SERVICE_STOP_PENDING = 3
            Stop.Cancel();
        }
        else if (control == ControlInterrogate)
        {
            return 0;
        }

        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Report(int state, int accepted, int exitCode = 0)
    {
        var s = new Status
        {
            ServiceType = OwnProcess,
            CurrentState = state,
            ControlsAccepted = accepted,
            Win32ExitCode = exitCode,
            WaitHint = state == StateRunning || state == StateStopped ? 0 : 30_000,
        };
        _ = SetServiceStatus(_statusHandle, &s);
    }
}
