using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Polaris.Agent;

/// <summary>Starts the pupil's icon in a session as the signed-in user, from the service (only the system can do this).
/// The icon gets a security descriptor that leaves its user only seeing it – not ending, pausing, debugging or changing
/// it. Task Manager, taskkill or Stop-Process of a pupil end with "access denied". Administrators and the system keep
/// full rights. Objects the icon makes later (its threads) get the same protection through the default DACL of the token.</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class IconLauncher
{
    public const string ProgramName = "polaris-ziak.exe";

    // SY, BA: everything. The owner (OW – this also takes away the owner's hidden right to change the DACL) and everyone
    // else (WD): only to see that the process exists and to wait for its end.
    public const string ProcessSddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x101000;;;OW)(A;;0x101000;;;WD)";
    public const string ThreadSddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x100800;;;OW)(A;;0x100800;;;WD)";
    public const string DefaultSddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x100000;;;OW)(A;;0x100000;;;WD)";

    public static string InstalledProgram => Path.Combine(Path.GetDirectoryName(AgentService.InstalledProgram)!, ProgramName);

    /// <summary>Sessions with a signed-in user that is at the PC or connected remotely (not session 0 of services).</summary>
    public static List<int> ActiveSessions()
    {
        var list = new List<int>();
        if (WTSEnumerateSessions(0, 0, 1, out var buffer, out var count) == 0)
        {
            return list;
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                var info = ((WTS_SESSION_INFOW*)buffer)[i];
                if (info.SessionId != 0 && info.State == 0 /* WTSActive */)
                {
                    list.Add(info.SessionId);
                }
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return list;
    }

    public static IIconProcess Start(int session, string program)
    {
        if (!File.Exists(program))
        {
            throw new FileNotFoundException("Program ikony chýba: " + program);
        }

        if (WTSQueryUserToken(session, out var userToken) == 0)
        {
            throw new SystemCallException($"Konto prihlásené v relácii {session}", Marshal.GetLastPInvokeError());
        }

        nint token = 0, environment = 0, processSd = 0, threadSd = 0, defaultSd = 0, desktop = 0;
        try
        {
            if (DuplicateTokenEx(userToken, 0x02000000 /* MAXIMUM_ALLOWED */, 0, 2 /* impersonation */, 1 /* primary */, out token) == 0)
            {
                throw new SystemCallException("Kópia tokenu používateľa", Marshal.GetLastPInvokeError());
            }

            processSd = Descriptor(ProcessSddl);
            threadSd = Descriptor(ThreadSddl);
            defaultSd = Descriptor(DefaultSddl);
            if (GetSecurityDescriptorDacl(defaultSd, out _, out var defaultDacl, out _) == 0)
            {
                throw new SystemCallException("Predvolená ochrana", Marshal.GetLastPInvokeError());
            }

            var dacl = defaultDacl;
            if (SetTokenInformation(token, 6 /* TokenDefaultDacl */, &dacl, (uint)sizeof(nint)) == 0)
            {
                throw new SystemCallException("Predvolená ochrana tokenu", Marshal.GetLastPInvokeError());
            }

            if (CreateEnvironmentBlock(out environment, token, 0) == 0)
            {
                throw new SystemCallException("Prostredie používateľa", Marshal.GetLastPInvokeError());
            }

            desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
            var startup = new STARTUPINFOW { cb = sizeof(STARTUPINFOW), lpDesktop = desktop };
            var processAttributes = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = processSd };
            var threadAttributes = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = threadSd };
            var command = $"\"{program}\" --zo-sluzby\0".ToCharArray();
            fixed (char* commandLine = command)
            {
                if (CreateProcessAsUser(token, program, commandLine, ref processAttributes, ref threadAttributes, 0,
                    0x00000400 /* CREATE_UNICODE_ENVIRONMENT */, environment, Path.GetDirectoryName(program), ref startup, out var created) == 0)
                {
                    throw new SystemCallException("Spustenie ikony", Marshal.GetLastPInvokeError());
                }

                CloseHandle(created.hThread);
                return new IconProcess(new SafeProcessHandle(created.hProcess, ownsHandle: true));
            }
        }
        finally
        {
            CloseHandle(userToken);
            if (token != 0)
            {
                CloseHandle(token);
            }

            if (environment != 0)
            {
                DestroyEnvironmentBlock(environment);
            }

            foreach (var sd in new[] { processSd, threadSd, defaultSd })
            {
                if (sd != 0)
                {
                    LocalFree(sd);
                }
            }

            if (desktop != 0)
            {
                Marshal.FreeHGlobal(desktop);
            }
        }
    }

    /// <summary>The DACL of a running process in SDDL (for checks).</summary>
    public static string ProtectionOf(SafeProcessHandle process)
    {
        var status = GetSecurityInfo(process.DangerousGetHandle(), 6 /* SE_KERNEL_OBJECT */, 4 /* DACL */, 0, 0, out _, 0, out var sd);
        if (status != 0)
        {
            throw new SystemCallException("Ochrana procesu", status);
        }

        try
        {
            if (ConvertSecurityDescriptorToStringSecurityDescriptor(sd, 1, 4, out var text, out _) == 0)
            {
                throw new SystemCallException("Ochrana procesu ako text", Marshal.GetLastPInvokeError());
            }

            var result = Marshal.PtrToStringUni(text) ?? "";
            LocalFree(text);
            return result;
        }
        finally
        {
            LocalFree(sd);
        }
    }

    private static nint Descriptor(string sddl) =>
        ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var sd, 0) != 0
            ? sd
            : throw new SystemCallException("Popis ochrany " + sddl, Marshal.GetLastPInvokeError());

    private sealed class IconProcess(SafeProcessHandle handle) : IIconProcess
    {
        public SafeProcessHandle Handle => handle;

        public bool HasExited => WaitForSingleObject(handle.DangerousGetHandle(), 0) == 0;

        public void Stop() => TerminateProcess(handle.DangerousGetHandle(), 0);

        public void Dispose() => handle.Dispose();
    }

    /// <summary>For a check on a PC: starts a process with the protection the icon gets, as the current user.</summary>
    public static SafeProcessHandle StartProtected(string program, string arguments)
    {
        var processSd = Descriptor(ProcessSddl);
        var threadSd = Descriptor(ThreadSddl);
        try
        {
            var startup = new STARTUPINFOW { cb = sizeof(STARTUPINFOW) };
            var pa = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = processSd };
            var ta = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = threadSd };
            var command = $"\"{program}\" {arguments}\0".ToCharArray();
            fixed (char* commandLine = command)
            {
                if (CreateProcess(program, commandLine, ref pa, ref ta, 0, 0x08000000 /* CREATE_NO_WINDOW */, 0, null, ref startup, out var created) == 0)
                {
                    throw new SystemCallException("Spustenie chráneného procesu", Marshal.GetLastPInvokeError());
                }

                CloseHandle(created.hThread);
                return new SafeProcessHandle(created.hProcess, ownsHandle: true);
            }
        }
        finally
        {
            LocalFree(processSd);
            LocalFree(threadSd);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFOW
    {
        public int SessionId;
        public nint pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW")]
    private static partial int WTSEnumerateSessions(nint server, int reserved, int version, out nint sessions, out int count);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    private static partial int WTSQueryUserToken(int session, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int SetTokenInformation(nint token, int infoClass, void* info, uint length);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, int revision, out nint descriptor, nint size);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW", SetLastError = true)]
    private static partial int ConvertSecurityDescriptorToStringSecurityDescriptor(nint descriptor, int revision, int information, out nint text, out int length);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int GetSecurityDescriptorDacl(nint descriptor, out int present, out nint dacl, out int defaulted);

    [LibraryImport("advapi32.dll")]
    private static partial int GetSecurityInfo(nint handle, int objectType, int information, nint owner, nint group, out nint dacl, nint sacl, out nint descriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateProcessAsUser(nint token, string application, char* commandLine, ref SECURITY_ATTRIBUTES process, ref SECURITY_ATTRIBUTES thread, int inherit, uint flags, nint environment, string? directory, ref STARTUPINFOW startup, out PROCESS_INFORMATION information);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateProcess(string application, char* commandLine, ref SECURITY_ATTRIBUTES process, ref SECURITY_ATTRIBUTES thread, int inherit, uint flags, nint environment, string? directory, ref STARTUPINFOW startup, out PROCESS_INFORMATION information);

    [LibraryImport("userenv.dll", SetLastError = true)]
    private static partial int CreateEnvironmentBlock(out nint environment, nint token, int inherit);

    [LibraryImport("userenv.dll")]
    private static partial int DestroyEnvironmentBlock(nint environment);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    private static partial int TerminateProcess(nint process, uint code);
}
