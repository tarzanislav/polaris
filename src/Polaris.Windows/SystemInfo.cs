using System.Globalization;
using Microsoft.Win32;

namespace Polaris.Windows;

/// <summary>Facts about this Windows needed by the checks – read from structured sources only.</summary>
public static unsafe class SystemInfo
{
    public static bool IsUefi() => Kernel32.GetFirmwareType(out var type) && type == 2;

    /// <summary>The boot entry this Windows was started from (what bcdedit calls {current}).</summary>
    public static Guid BootIdentifier()
    {
        Ntdll.BootEnvironment info;
        var status = Ntdll.NtQuerySystemInformation(Ntdll.SystemBootEnvironmentInformation, &info, sizeof(Ntdll.BootEnvironment), out _);
        if (status != 0)
        {
            throw new SystemCallException("Identifikátor aktuálneho štartu", status);
        }

        return info.BootIdentifier;
    }

    public static (string Edition, string Product, string Version, int Build, int Revision) WindowsVersion()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var edition = k?.GetValue("EditionID") as string ?? "";
        var product = k?.GetValue("ProductName") as string ?? "";
        var display = k?.GetValue("DisplayVersion") as string ?? "";
        _ = int.TryParse(k?.GetValue("CurrentBuildNumber") as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var build);
        var ubr = k?.GetValue("UBR") is int u ? u : 0;
        return (edition, product, display, build, ubr);
    }

    public static long MemoryBytes()
    {
        var status = new Kernel32.MemoryStatus { Length = (uint)sizeof(Kernel32.MemoryStatus) };
        return Kernel32.GlobalMemoryStatusEx(&status) ? (long)status.TotalPhys : 0;
    }

    /// <summary>Is a restart pending (an update or a rename waits for it)? A picture of such a system would be half done.</summary>
    public static List<string> PendingRestart()
    {
        var reasons = new List<string>();
        using (var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
        {
            if (cbs is not null)
            {
                reasons.Add("údržba súčastí Windows");
            }
        }

        using (var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
        {
            if (wu is not null)
            {
                reasons.Add("Windows Update");
            }
        }

        using (var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
        {
            if (sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
            {
                reasons.Add("premenovanie súborov pri štarte");
            }
        }

        return reasons;
    }

    public static bool HibernationEnabled()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
        return k?.GetValue("HibernateEnabled") is not int h || h != 0;
    }

    public static bool FastStartupEnabled()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
        return k?.GetValue("HiberbootEnabled") is int h && h != 0;
    }

    public static bool SecureBoot()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return k?.GetValue("UEFISecureBootEnabled") is int v && v != 0;
    }

    public static string ProcessorName()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (k?.GetValue("ProcessorNameString") as string ?? "neznámy").Trim();
    }

    /// <summary>Turns hibernation and fast startup off (both needed for starting from a VHDX).</summary>
    public static void DisableHibernation()
    {
        using (var sm = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power"))
        {
            sm.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
        }

        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/hibernate off")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("Hibernáciu sa nepodarilo vypnúť (powercfg " + p.ExitCode + ").");
        }
    }

    public static void Restart(string message, bool force)
    {
        Privileges.Enable(Privileges.Shutdown);
        const uint restart = 0x4;
        const uint forceOthers = 0x1;
        const uint planned = 0x80000000;
        var result = Advapi32.InitiateShutdown(null, message, force ? 0u : 5u, restart | (force ? forceOthers : 0), planned);
        if (result != 0)
        {
            throw new SystemCallException("Reštart", (int)result);
        }
    }
}
