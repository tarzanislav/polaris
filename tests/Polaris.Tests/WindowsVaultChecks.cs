using System.Runtime.Versioning;
using System.Security.Principal;
using Polaris.Agent;

namespace Polaris.Tests;

/// <summary>The trezor of profiles on a real Windows. Saving a profile needs the backup and restore privileges the
/// service's system account has; the runner's administrator does not have them, so CI runs the same check as the
/// system (polaris-agent skuska-trezoru). Here it runs where the privileges are.</summary>
public static class WindowsVaultChecks
{
    [Check]
    public static void AProfileIsSavedAndPutBackExactly()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin() || !HasPrivileges())
        {
            return;
        }

        foreach (var (ok, what) in VaultSelfTest.Run())
        {
            Expect.True(ok, what);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasPrivileges()
    {
        try
        {
            Polaris.Windows.Privileges.Enable(Polaris.Windows.Privileges.Backup);
            Polaris.Windows.Privileges.Enable(Polaris.Windows.Privileges.Restore);
            return true;
        }
        catch (Polaris.Windows.SystemCallException)
        {
            return false;
        }
    }

    private static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
