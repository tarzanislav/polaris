using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Polaris.Agent;

/// <summary>A check of the pupil's icon in a pupil's position, run by an administrator (polaris-agent skuska-ikony): the
/// same account with the Administrators group switched off and itself as the owner – exactly a pupil's token. The icon is
/// started with each part of its protection on and off; for each it says whether the icon stayed alive.</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class IconSelfTest
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);

    public static readonly IReadOnlyList<(string Name, IconProtection Protection)> Variants =
    [
        ("plná ochrana", IconProtection.Full),
        ("bez predvolenej ochrany", new IconProtection(Process: true, Threads: true, DefaultSddl: null)),
        ("predvolená bez ukončenia a pozastavenia (0x1303FC)", new IconProtection(true, true, "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x1303FC;;;OW)(A;;0x100000;;;WD)")),
        ("predvolená bez ukončenia (0x1303FE)", new IconProtection(true, true, "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x1303FE;;;OW)(A;;0x100000;;;WD)")),
        ("predvolená bez zmeny ochrany (0x1303FF)", new IconProtection(true, true, "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x1303FF;;;OW)(A;;0x100000;;;WD)")),
        ("bez ochrany", new IconProtection(Process: false, Threads: false, DefaultSddl: null)),
    ];

    /// <summary>For each variant: did the icon live <see cref="Wait"/>, and its exit code if not.</summary>
    public static List<(string Name, bool Alive, uint? ExitCode)> Run(string program, TextWriter output)
    {
        var results = new List<(string, bool, uint?)>();
        foreach (var (name, protection) in Variants)
        {
            var token = PupilToken();
            try
            {
                using var icon = IconLauncher.StartWithToken(token, program, "--zo-sluzby", protection, desktop: null);
                var until = DateTime.UtcNow + Wait;
                while (DateTime.UtcNow < until && !icon.HasExited)
                {
                    Thread.Sleep(250);
                }

                var alive = !icon.HasExited;
                var code = icon.ExitCode;
                if (alive)
                {
                    icon.Stop();
                }

                results.Add((name, alive, code));
                output.WriteLine($"  {(alive ? "žije" : "padla")}  {name}{(alive ? "" : IconWatch.ExitText(code))}");
            }
            finally
            {
                CloseHandle(token);
            }
        }

        return results;
    }

    /// <summary>The current admin's token without the Administrators group and without privileges, owned by the user.</summary>
    private static nint PupilToken()
    {
        if (OpenProcessToken(-1, 0x02000000 /* MAXIMUM_ALLOWED */, out var own) == 0)
        {
            throw new SystemCallException("Token procesu", Marshal.GetLastPInvokeError());
        }

        try
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var adminsBytes = new byte[admins.BinaryLength];
            admins.GetBinaryForm(adminsBytes, 0);
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("Konto bez SID.");
            var userBytes = new byte[user.BinaryLength];
            user.GetBinaryForm(userBytes, 0);
            fixed (byte* a = adminsBytes)
            fixed (byte* u = userBytes)
            {
                var disable = new SidAndAttributes { Sid = (nint)a };
                if (CreateRestrictedToken(own, 0x1 /* DISABLE_MAX_PRIVILEGE */, 1, &disable, 0, 0, 0, 0, out var pupil) == 0)
                {
                    throw new SystemCallException("Token žiaka", Marshal.GetLastPInvokeError());
                }

                var owner = (nint)u; // TOKEN_OWNER: a pointer to the owner's SID
                if (SetTokenInformation(pupil, 4 /* TokenOwner */, &owner, (uint)sizeof(nint)) == 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    CloseHandle(pupil);
                    throw new SystemCallException("Vlastník v tokene žiaka", error);
                }

                return pupil;
            }
        }
        finally
        {
            CloseHandle(own);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int CreateRestrictedToken(nint token, uint flags, uint disableCount, SidAndAttributes* disable, uint deleteCount, nint delete, uint restrictCount, nint restrict, out nint newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int SetTokenInformation(nint token, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
