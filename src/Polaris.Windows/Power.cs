using System.Runtime.InteropServices;

namespace Polaris.Windows;

/// <summary>A request of Windows that the PC must not fall asleep (a power request "system required"), with a reason
/// people see in <c>powercfg /requests</c>. It holds until disposed – also from a service, for any thread. It does not
/// stop a person who puts the PC to sleep himself, only the sleep after idle time.</summary>
public sealed unsafe partial class AwakeRequest : IDisposable
{
    private const uint ContextVersion = 0;
    private const uint SimpleString = 0x1;
    private const int SystemRequired = 1;

    private nint _handle;

    public AwakeRequest(string reason)
    {
        var text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext { Version = ContextVersion, Flags = SimpleString, Reason = text };
            _handle = PowerCreateRequest(&context); // Windows copies the reason
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }

        if (_handle is 0 or -1)
        {
            throw new SystemCallException("Požiadavka, aby PC nezaspalo", Marshal.GetLastPInvokeError());
        }

        if (PowerSetRequest(_handle, SystemRequired) == 0)
        {
            var code = Marshal.GetLastPInvokeError();
            _ = Kernel32.CloseHandle(_handle);
            _handle = 0;
            throw new SystemCallException("Požiadavka, aby PC nezaspalo", code);
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            _ = PowerClearRequest(_handle, SystemRequired);
            _ = Kernel32.CloseHandle(_handle);
            _handle = 0;
        }
    }

    // REASON_CONTEXT: the union is as big as its detailed form (module, id, count, strings)
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint Reason;
        public nint Unused1;
        public nint Unused2;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint PowerCreateRequest(ReasonContext* context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int PowerSetRequest(nint request, int type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int PowerClearRequest(nint request, int type);
}

/// <summary>The memory of the PC as Windows counts it.</summary>
public static unsafe partial class MemoryStatus
{
    /// <summary>All the physical memory and the part still available, in bytes.</summary>
    public static (ulong Total, ulong Available) Physical()
    {
        var status = new MEMORYSTATUSEX { Length = (uint)sizeof(MEMORYSTATUSEX) };
        Check.Win32(GlobalMemoryStatusEx(&status) != 0, "Stav pamäte");
        return (status.TotalPhys, status.AvailPhys);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GlobalMemoryStatusEx(MEMORYSTATUSEX* status);
}

/// <summary>A signed-in session as the terminal services of Windows know it.</summary>
public sealed record SessionLogon(int Session, string Account, DateTimeOffset LogonUtc, DateTimeOffset NowUtc);

/// <summary>One answer of the terminal services about a session, as read (also for a session without a user).</summary>
public sealed record SessionAnswer(int Session, string User, long LogonFileTime, long CurrentFileTime);

public static unsafe partial class Logons
{
    // WTSINFOW: state, id, six counters, the station name (32 chars), domain (17), user (21), then five times
    private const int InfoSize = 216;
    private const int IdAt = 4;
    private const int UserAt = 130;
    private const int UserChars = 21;
    private const int LogonTimeAt = 200;
    private const int CurrentTimeAt = 208;
    private const int SessionInfo = 24; // WTSSessionInfo

    /// <summary>When the user of the session signed in; null for a session without a user or when Windows does not say.
    /// The time of Windows in the same answer must be now – a check that the answer is read right.</summary>
    public static SessionLogon? Of(int session)
    {
        if (Query(session) is not { } a || a.LogonFileTime <= 0 || a.CurrentFileTime <= 0 || a.User.Length == 0)
        {
            return null;
        }

        var now = DateTimeOffset.FromFileTime(a.CurrentFileTime).ToUniversalTime();
        return Math.Abs((now - DateTimeOffset.UtcNow).TotalHours) > 1
            ? null
            : new SessionLogon(session, a.User, DateTimeOffset.FromFileTime(a.LogonFileTime).ToUniversalTime(), now);
    }

    /// <summary>The raw answer, or null when Windows gives none or one too short or about another session.</summary>
    public static SessionAnswer? Query(int session)
    {
        if (WTSQuerySessionInformation(0, session, SessionInfo, out var buffer, out var bytes) == 0)
        {
            return null;
        }

        try
        {
            if (bytes < InfoSize || *(int*)(buffer + IdAt) != session)
            {
                return null;
            }

            var user = new string((char*)(buffer + UserAt), 0, UserChars).Split('\0')[0];
            return new SessionAnswer(session, user, *(long*)(buffer + LogonTimeAt), *(long*)(buffer + CurrentTimeAt));
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    private static partial int WTSQuerySessionInformation(nint server, int session, int infoClass, out nint buffer, out int bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
