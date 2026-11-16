using System.Runtime.InteropServices;
using System.Security.Principal;
using Polaris.Agent;

namespace Polaris.Tests;

/// <summary>The protection of the pupil's icon on a real Windows: a process started with it cannot be ended by its
/// own user without administrator rights. The runner is an administrator, so the check takes the same account with
/// the Administrators group switched off (a restricted token) – that is exactly a pupil's position.</summary>
public static partial class WindowsIconChecks
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSuspendResume = 0x0800;
    private const uint WriteDac = 0x00040000;
    private const uint ProcessQueryLimited = 0x1000;

    [Check]
    public static void ThePupilCannotEndTheIcon()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return;
        }

        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var process = IconLauncher.StartProtected(cmd, "/c ping -n 60 127.0.0.1 >nul");
        var pid = GetProcessId(process.DangerousGetHandle());
        try
        {
            var dacl = IconLauncher.ProtectionOf(process);
            Expect.True(dacl.Contains("(A;;0x101000;;;OW)", StringComparison.Ordinal) && dacl.Contains("(A;;0x101000;;;WD)", StringComparison.Ordinal), "ochrana procesu: " + dacl);

            // as the pupil: the same account, the Administrators group only for denying
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var adminsBytes = new byte[admins.BinaryLength];
            admins.GetBinaryForm(adminsBytes, 0);
            var pinned = GCHandle.Alloc(adminsBytes, GCHandleType.Pinned);
            try
            {
                var disable = new SID_AND_ATTRIBUTES { Sid = pinned.AddrOfPinnedObject() };
                Expect.True(CreateRestrictedToken(identity.Token, 0x1 /* DISABLE_MAX_PRIVILEGE */, 1, ref disable, 0, 0, 0, 0, out var pupil) != 0, "obmedzený token");
                try
                {
                    Expect.True(ImpersonateLoggedOnUser(pupil) != 0, "konať ako žiak");
                    try
                    {
                        foreach (var (right, what) in new[] { (ProcessTerminate, "ukončiť"), (ProcessSuspendResume, "pozastaviť"), (WriteDac, "zmeniť ochranu") })
                        {
                            var h = OpenProcess(right, 0, pid);
                            var error = Marshal.GetLastPInvokeError();
                            if (h != 0)
                            {
                                CloseHandle(h);
                            }

                            Expect.True(h == 0 && error == 5, $"žiak nesmie {what} (chyba {error})");
                        }

                        var look = OpenProcess(ProcessQueryLimited, 0, pid);
                        Expect.True(look != 0, "žiak ho vidí v Správcovi úloh");
                        CloseHandle(look);
                    }
                    finally
                    {
                        RevertToSelf();
                    }
                }
                finally
                {
                    CloseHandle(pupil);
                }
            }
            finally
            {
                pinned.Free();
            }

            var asAdmin = OpenProcess(ProcessTerminate, 0, pid);
            Expect.True(asAdmin != 0, "správca ho ukončiť smie");
            CloseHandle(asAdmin);
        }
        finally
        {
            TerminateProcess(process.DangerousGetHandle(), 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public nint Sid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int CreateRestrictedToken(nint token, uint flags, uint disableCount, ref SID_AND_ATTRIBUTES disable, uint deleteCount, nint delete, uint restrictCount, nint restrict, out nint newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int ImpersonateLoggedOnUser(nint token);

    [LibraryImport("advapi32.dll")]
    private static partial int RevertToSelf();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, int inherit, int pid);

    [LibraryImport("kernel32.dll")]
    private static partial int GetProcessId(nint process);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial int TerminateProcess(nint process, uint code);
}
