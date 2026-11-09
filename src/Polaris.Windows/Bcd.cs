using System.Diagnostics;
using Microsoft.Win32;
using Polaris.Core;

namespace Polaris.Windows;

/// <summary>The boot configuration (BCD). Reading goes through its registry form – structured and the same in every
/// language. Writing goes through bcdedit with fixed arguments; only its exit code matters, its translated text goes
/// to the journal only. A store file (for tests) or the store of this system.</summary>
public sealed class BcdStore(string? storeFile = null)
{
    private static readonly string BcdEdit = Path.Combine(Environment.SystemDirectory, "bcdedit.exe");

    public string? StoreFile { get; } = storeFile;

    public Action<string>? Log { get; set; }

    public static BcdStore ThisSystem { get; } = new();

    // ---- reading ----

    public BootManagerSettings ReadBootManager() => Read(objects =>
    {
        using var mgr = objects.OpenSubKey(BcdModel.ObjectKey(BcdModel.BootManager) + @"\Elements");
        if (mgr is null)
        {
            throw new InvalidOperationException("V konfigurácii štartu chýba správca štartu.");
        }

        return new BootManagerSettings
        {
            Default = Guid.TryParse(String(mgr, BcdModel.DefaultObject), out var d) ? d : null,
            DisplayOrder = BcdModel.ParseList(Value(mgr, BcdModel.DisplayOrder) as string[]),
            BootSequence = BcdModel.ParseList(Value(mgr, BcdModel.BootSequence) as string[]),
            Timeout = BcdModel.ParseInteger(Value(mgr, BcdModel.Timeout) as byte[]),
        };
    });

    public bool Exists(Guid id) => Read(objects =>
    {
        using var key = objects.OpenSubKey(BcdModel.ObjectKey(id));
        return key is not null;
    });

    public string? Description(Guid id) => Read(objects =>
    {
        using var e = objects.OpenSubKey(BcdModel.ObjectKey(id) + @"\Elements");
        return e is null ? null : String(e, BcdModel.Description);
    });

    public byte[]? Device(Guid id, uint element = BcdModel.ApplicationDevice) => Read(objects =>
    {
        using var e = objects.OpenSubKey(BcdModel.ObjectKey(id) + @"\Elements");
        return e is null ? null : Value(e, element) as byte[];
    });

    /// <summary>All boot entries with a description (identifier, description).</summary>
    public List<(Guid Id, string Description)> Entries() => Read(objects =>
    {
        var list = new List<(Guid, string)>();
        foreach (var name in objects.GetSubKeyNames())
        {
            if (Guid.TryParse(name, out var id))
            {
                using var e = objects.OpenSubKey(name + @"\Elements");
                if (e is not null && String(e, BcdModel.Description) is { } d)
                {
                    list.Add((id, d));
                }
            }
        }

        return list;
    });

    private T Read<T>(Func<RegistryKey, T> reader)
    {
        if (StoreFile is null)
        {
            using var objects = Registry.LocalMachine.OpenSubKey(@"BCD00000000\Objects")
                ?? throw new InvalidOperationException("Konfigurácia štartu (BCD00000000) nie je načítaná.");
            return reader(objects);
        }

        using var hive = LoadedHive.Load(StoreFile, "POLARIS_BCD_SKUSKA");
        using (var objects = hive.Open("Objects", writable: false))
        {
            return reader(objects);
        }
    }

    private static object? Value(RegistryKey elements, uint type)
    {
        using var k = elements.OpenSubKey(BcdModel.ElementKey(type));
        return k?.GetValue("Element");
    }

    private static string? String(RegistryKey elements, uint type) => Value(elements, type) as string;

    // ---- writing ----

    /// <summary>Runs bcdedit; returns its output. A non-zero exit code is an error.</summary>
    public string Run(params string[] args)
    {
        var info = new ProcessStartInfo(BcdEdit)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (StoreFile is not null)
        {
            info.ArgumentList.Add("/store");
            info.ArgumentList.Add(StoreFile);
        }

        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("bcdedit sa nespustil.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Log?.Invoke($"bcdedit {string.Join(' ', args)} → {process.ExitCode}");
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"bcdedit {string.Join(' ', args)} skončil kódom {process.ExitCode}: {output.Trim()}");
        }

        return output;
    }

    public void Export(string file) => Run("/export", file);

    /// <summary>Copies a boot entry (with all its settings) under a new description; returns the new identifier.</summary>
    public Guid Copy(Guid source, string description)
    {
        var id = BcdModel.SingleGuidIn(Run("/copy", BcdModel.Arg(source), "/d", description));
        if (!Exists(id))
        {
            throw new InvalidOperationException("Skopírovaný záznam štartu sa v konfigurácii nenašiel: " + id.ToString("B"));
        }

        return id;
    }

    /// <summary>The entry starts Windows from a VHDX: path like [C:]\POLARIS\Sklad\relacia-A.vhdx.</summary>
    public void SetVhd(Guid id, string driveLetter, string pathOnVolume)
    {
        var device = $"vhd=[{driveLetter}]{pathOnVolume}";
        Run("/set", BcdModel.Arg(id), "device", device);
        Run("/set", BcdModel.Arg(id), "osdevice", device);
        if (!BcdModel.DeviceContainsPath(Device(id), pathOnVolume) || !BcdModel.DeviceContainsPath(Device(id, BcdModel.OsDevice), pathOnVolume))
        {
            throw new InvalidOperationException("Záznam štartu neukazuje na " + pathOnVolume);
        }
    }

    public void Set(Guid id, string element, string value) => Run("/set", BcdModel.Arg(id), element, value);

    public void SetDefault(Guid id)
    {
        Run("/default", BcdModel.Arg(id));
        Verify(s => s.Default == id, "predvolený štart");
    }

    public void SetBootSequence(Guid id)
    {
        Run("/bootsequence", BcdModel.Arg(id));
        Verify(s => s.BootSequence.SequenceEqual([id]), "jednorazový štart");
    }

    public void ClearBootSequence()
    {
        if (ReadBootManager().BootSequence.Count > 0)
        {
            Run("/deletevalue", "{bootmgr}", "bootsequence");
        }
    }

    public void SetDisplayOrder(IReadOnlyList<Guid> order)
    {
        Run(["/displayorder", .. order.Select(BcdModel.Arg)]);
        Verify(s => s.DisplayOrder.SequenceEqual(order), "ponuka štartu");
    }

    public void SetTimeout(long seconds) => Run("/timeout", seconds.ToString(global::System.Globalization.CultureInfo.InvariantCulture));

    public void Delete(Guid id)
    {
        if (Exists(id))
        {
            Run("/delete", BcdModel.Arg(id), "/cleanup");
        }
    }

    private void Verify(Func<BootManagerSettings, bool> ok, string what)
    {
        if (!ok(ReadBootManager()))
        {
            throw new InvalidOperationException("Konfigurácia štartu po zápise nesedí: " + what);
        }
    }
}
