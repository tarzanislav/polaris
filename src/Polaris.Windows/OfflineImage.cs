using System.Globalization;
using Microsoft.Win32;
using Polaris.Core;

namespace Polaris.Windows;

/// <summary>Prepares the copied Windows (the base layer, mounted in a folder) before its first start: drive C: belongs
/// to the new partition, the disk does not grow at start, no hibernation, the agent service is registered.</summary>
public static class OfflineImage
{
    public const string ServiceName = "POLARIS";
    public const string HiveName = "POLARIS_ZAKLAD";

    public sealed record Changes(string MountedDevicesBefore, string ControlSet);

    /// <summary>Edits the SYSTEM registry of the image at <paramref name="root"/> (a folder with \Windows).</summary>
    /// <param name="root">Where the image is mounted.</param>
    /// <param name="partitionId">The GPT partition of the image – it becomes C:.</param>
    /// <param name="agentCommand">The command line of the service inside the image.</param>
    public static Changes Prepare(string root, Guid partitionId, string agentCommand)
    {
        var hiveFile = Path.Combine(root, @"Windows\System32\config\SYSTEM");
        if (!File.Exists(hiveFile))
        {
            throw new FileNotFoundException("V kópii chýba register SYSTEM.", hiveFile);
        }

        using var hive = LoadedHive.Load(hiveFile, HiveName);
        string before;
        using (var mounted = hive.Open("MountedDevices", writable: true))
        {
            var oldC = mounted.GetValue(@"\DosDevices\C:") as byte[];
            before = oldC is null ? "nebolo" : MountedDevices.TryReadGpt(oldC, out var oldId) ? oldId.ToString("B") : Convert.ToHexString(oldC);
            mounted.SetValue(@"\DosDevices\C:", MountedDevices.GptValue(partitionId), RegistryValueKind.Binary);
        }

        int current;
        using (var select = hive.Open("Select", writable: false))
        {
            current = Convert.ToInt32(select.GetValue("Current") ?? 1, CultureInfo.InvariantCulture);
        }

        var set = "ControlSet" + current.ToString("000", CultureInfo.InvariantCulture);
        using (var fs = hive.Create(set + @"\Services\FsDepends\Parameters"))
        {
            // the difference layer must not be stretched to the full size of the disk at every start
            fs.SetValue("VirtualDiskExpandOnMount", 4, RegistryValueKind.DWord);
        }

        using (var power = hive.Create(set + @"\Control\Power"))
        {
            power.SetValue("HibernateEnabled", 0, RegistryValueKind.DWord);
            power.SetValue("HibernateEnabledDefault", 0, RegistryValueKind.DWord);
        }

        using (var smPower = hive.Create(set + @"\Control\Session Manager\Power"))
        {
            smPower.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
        }


        using (var service = hive.Create(set + @"\Services\" + ServiceName))
        {
            service.SetValue("Type", 0x10, RegistryValueKind.DWord);
            service.SetValue("Start", 2, RegistryValueKind.DWord);
            service.SetValue("ErrorControl", 1, RegistryValueKind.DWord);
            service.SetValue("ImagePath", agentCommand, RegistryValueKind.ExpandString);
            service.SetValue("ObjectName", "LocalSystem", RegistryValueKind.String);
            service.SetValue("DisplayName", $"{Product.Name} – zmrazenie (laboratórium {Product.Stage})", RegistryValueKind.String);
            service.SetValue("Description", "Pripravuje čisté vrstvy na ďalší štart a zapisuje denník skúšky.", RegistryValueKind.String);
            // restart after 10 s when the agent ends by a failure
            service.SetValue("FailureActions", FailureActions(), RegistryValueKind.Binary);
        }

        return new Changes(before, set);
    }

    /// <summary>SERVICE_FAILURE_ACTIONS as stored in the registry: reset period, no reboot message, no command, three
    /// actions "restart after 10 seconds".</summary>
    private static byte[] FailureActions()
    {
        var data = new byte[20 + 3 * 8];
        BitConverter.TryWriteBytes(data.AsSpan(0), 86400); // reset the count after a day
        BitConverter.TryWriteBytes(data.AsSpan(12), 3);
        BitConverter.TryWriteBytes(data.AsSpan(16), 20); // where the actions start (a non-zero value marks them present)
        for (var i = 0; i < 3; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(20 + 8 * i), 1); // SC_ACTION_RESTART
            BitConverter.TryWriteBytes(data.AsSpan(24 + 8 * i), 10000);
        }

        return data;
    }

    /// <summary>Copies the programs into the image and puts a shortcut for the owner on the common desktop.</summary>
    public static void InstallFiles(string root, string labExe)
    {
        var programDir = Path.Combine(root, @"Program Files\POLARIS");
        Directory.CreateDirectory(programDir);
        CopyClean(labExe, Path.Combine(programDir, "polaris-lab.exe"));
        var desktop = Path.Combine(root, @"Users\Public\Desktop");
        Directory.CreateDirectory(desktop);
        File.WriteAllText(
            Path.Combine(desktop, "POLARIS laboratorium.cmd"),
            "@echo off\r\nstart \"\" \"%ProgramFiles%\\POLARIS\\polaris-lab.exe\"\r\n",
            Serialization.Utf8);
    }

    /// <summary>A copy without the mark of a downloaded file (Zone.Identifier).</summary>
    public static void CopyClean(string from, string to)
    {
        File.Copy(from, to, overwrite: true);
        _ = Kernel32.DeleteFile(to + ":Zone.Identifier");
    }
}
