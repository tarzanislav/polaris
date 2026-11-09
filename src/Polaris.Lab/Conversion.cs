using System.Diagnostics;
using System.Security.Principal;
using Polaris.Core;
using Polaris.Windows;

namespace Polaris.Lab;

/// <summary>What the check found out about the PC (needed by the conversion afterwards).</summary>
internal sealed class PcFacts
{
    public List<CheckResult> Results { get; } = [];

    public Guid CurrentBoot { get; set; }

    public BootManagerSettings Boot { get; set; } = new();

    public long UsedBytes { get; set; }

    public long FreeBytes { get; set; }

    public long Memory { get; set; }

    public bool Hibernation { get; set; }

    public List<(Guid Id, string Description)> OldEntries { get; } = [];

    public bool CanStart => CheckRules.CanStart(Results);

    public void Add(string name, CheckLevel level, string detail) => Results.Add(new CheckResult(name, level, detail));
}

/// <summary>The conversion of the running Windows into layers – without a USB key, without WinPE. The original Windows
/// stays untouched; the first start into the layer is a one-time start, so a failure falls back to it by itself.</summary>
internal static class Conversion
{
    public const string SystemDrive = "C:";

    public static PcFacts Inspect()
    {
        var f = new PcFacts();
        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        f.Add("Práva správcu", admin ? CheckLevel.Ok : CheckLevel.Blocker, admin ? "áno" : "spusti program ako správca");
        f.Add("Firmvér UEFI", SystemInfo.IsUefi() ? CheckLevel.Ok : CheckLevel.Blocker, SystemInfo.IsUefi() ? "áno" : "štart z VHDX vyžaduje UEFI");

        var (edition, product, display, build, revision) = SystemInfo.WindowsVersion();
        f.Add("Windows", CheckRules.EditionSupportsNativeBoot(edition) ? CheckLevel.Ok : CheckLevel.Blocker,
            $"{product} {display} (zostava {build}.{revision}, edícia {edition})");
        if (build is > 0 and < 26100)
        {
            f.Add("Verzia Windows", CheckLevel.Warning, "E0 je určené pre Windows 11 24H2 a novší");
        }

        f.Add("Procesor", CheckLevel.Info, SystemInfo.ProcessorName());
        f.Memory = SystemInfo.MemoryBytes();
        f.Add("Pamäť", CheckLevel.Info, Format.Gb(f.Memory));
        f.Add("Secure Boot", CheckLevel.Info, SystemInfo.SecureBoot() ? "zapnutý" : "vypnutý");

        try
        {
            using var volume = Disks.OpenVolume(@"\\.\" + SystemDrive, DeviceAccess.Read);
            var part = Disks.GetPartition(volume);
            f.Add("Tabuľka oddielov", part.IsGpt ? CheckLevel.Ok : CheckLevel.Blocker, part.IsGpt ? "GPT" : "MBR – štart z VHDX v E0 vyžaduje GPT");
            var boot = VolumeCopier.ReadBootSector(volume);
            f.Add("Šifrovanie BitLocker", boot.IsBitLocker ? CheckLevel.Blocker : CheckLevel.Ok,
                boot.IsBitLocker ? "zväzok C: je šifrovaný – najprv ho úplne dešifruj" : "vypnuté");
            f.Add("Súborový systém C:", boot.IsNtfs ? CheckLevel.Ok : CheckLevel.Blocker, boot.IsNtfs ? "NTFS" : "nie je NTFS");
            var extents = Disks.Extents(volume);
            if (extents.Count == 1)
            {
                using var disk = Disks.OpenDisk(extents[0].Disk, write: false);
                var bus = Disks.BusType(disk);
                f.Add("Disk", bus.StartsWith("virtuál", StringComparison.Ordinal) ? CheckLevel.Blocker : CheckLevel.Info,
                    bus.StartsWith("virtuál", StringComparison.Ordinal) ? "tento Windows už beží z virtuálneho disku" : bus);
            }
            else
            {
                f.Add("Disk", CheckLevel.Blocker, "zväzok C: leží na viacerých diskoch");
            }

            if (boot.IsNtfs)
            {
                var ntfs = Volumes.Ntfs(volume);
                f.UsedBytes = ntfs.UsedBytes;
            }
        }
        catch (SystemCallException e)
        {
            f.Add("Zväzok C:", CheckLevel.Blocker, e.Message);
        }

        var (free, total) = Volumes.Space(SystemDrive + "\\");
        f.FreeBytes = free;
        var need = CheckRules.RequiredFreeBytes(f.UsedBytes, f.Memory);
        f.Add("Miesto na C:", free >= need ? CheckLevel.Ok : CheckLevel.Blocker,
            $"voľné {Format.Gb(free)} z {Format.Gb(total)}, použité {Format.Gb(f.UsedBytes)}, treba aspoň {Format.Gb(need)}");

        var pending = SystemInfo.PendingRestart();
        f.Add("Čakajúci reštart", pending.Count == 0 ? CheckLevel.Ok : CheckLevel.Blocker,
            pending.Count == 0 ? "nie" : "najprv reštartuj PC (" + string.Join(", ", pending) + ")");

        f.Hibernation = SystemInfo.HibernationEnabled() || SystemInfo.FastStartupEnabled();
        f.Add("Hibernácia a rýchle spustenie", f.Hibernation ? CheckLevel.Warning : CheckLevel.Ok,
            f.Hibernation ? "zapnuté – prevod ich sám vypne (štart z VHDX ich nepodporuje)" : "vypnuté");

        try
        {
            f.CurrentBoot = SystemInfo.BootIdentifier();
            f.Boot = BcdStore.ThisSystem.ReadBootManager();
            var ok = f.Boot.Default == f.CurrentBoot;
            f.Add("Konfigurácia štartu", ok ? CheckLevel.Ok : CheckLevel.Blocker,
                ok ? "aktuálny Windows je predvolený" : "aktuálny Windows nie je predvolený štart – treba kontrolu");
            if (f.Boot.BootSequence.Count > 0)
            {
                f.Add("Jednorazový štart", CheckLevel.Blocker, "v konfigurácii čaká jednorazový štart – reštartuj PC a skús znova");
            }

            foreach (var (id, description) in BcdStore.ThisSystem.Entries())
            {
                if (description.Contains("ORR", StringComparison.Ordinal) || description.Contains("POLARIS", StringComparison.Ordinal))
                {
                    f.OldEntries.Add((id, description));
                }
            }

            if (f.OldEntries.Count > 0)
            {
                f.Add("Staré záznamy štartu", CheckLevel.Warning,
                    string.Join("; ", f.OldEntries.Select(e => e.Description)) + " – prevod ponúkne ich odstránenie");
            }
        }
        catch (Exception e) when (e is SystemCallException or InvalidOperationException)
        {
            f.Add("Konfigurácia štartu", CheckLevel.Blocker, e.Message);
        }

        var store = Store.OnSystemDrive();
        if (store.Exists || Directory.Exists(store.Root))
        {
            f.Add("POLARIS", CheckLevel.Blocker, "na tomto PC už je " + store.Root + " – použi Stav alebo Odinštalovať");
        }

        return f;
    }

    public static bool Run(PcFacts facts)
    {
        var store = Store.OnSystemDrive();
        var notes = new List<string>();
        void Log(string text)
        {
            notes.Add($"{DateTime.Now:HH:mm:ss} {text}");
            Ui.Line("  " + text);
        }

        var created = new List<Guid>();
        var clock = Stopwatch.StartNew();
        var createdRoot = false;
        try
        {
            if (facts.Hibernation)
            {
                Log("Vypínam hibernáciu a rýchle spustenie…");
                SystemInfo.DisableHibernation();
            }

            foreach (var (id, description) in facts.OldEntries)
            {
                Log("Odstraňujem starý záznam štartu: " + description);
                BcdStore.ThisSystem.Delete(id);
            }

            var physical = BootEvents.Recent(10).Select(b => b.Milliseconds).ToList();
            Log(physical.Count > 0
                ? $"Štarty pôvodného Windows podľa jeho meraní: priemer {Format.Seconds(physical.Average() / 1000)} ({physical.Count}×)."
                : "Windows zatiaľ nemá vlastné merania štartu (porovnanie bude neskôr).");

            Directory.CreateDirectory(store.Directory);
            createdRoot = true;
            Security.Protect(store.Root);
            Directory.CreateDirectory(Path.Combine(store.Root, "Pracovne"));
            Directory.CreateDirectory(Path.Combine(store.Root, "Zalohy"));
            Directory.CreateDirectory(Path.Combine(store.Root, "Program"));

            var state = new LabState
            {
                InstallId = Guid.NewGuid(),
                ProgramVersion = Product.Version,
                CreatedUtc = DateTimeOffset.UtcNow,
                Phase = LabPhase.Preparing,
                OriginalBcdId = facts.CurrentBoot,
                OriginalBoot = facts.Boot,
                PhysicalBootTimesMs = physical,
            };
            foreach (var slot in Slots.All)
            {
                state.Slots.Add(new SlotState { Slot = slot, File = Slots.FileName(slot) });
            }

            store.Save(state);
            store.Append(state, JournalKind.Install, $"Začiatok prevodu (program {Product.Version}).");

            var basePath = Path.Combine(store.Directory, state.BaseFile);
            var started = DateTime.UtcNow;
            var lastPercent = -5;
            var phase = "";
            var result = BaseBuilder.Build(SystemDrive + "\\", basePath, Log, (what, done, total) =>
            {
                if (what != phase)
                {
                    phase = what;
                    started = DateTime.UtcNow;
                    lastPercent = -5;
                }

                Ui.Progress(what, done, total, started);
                var pct = (int)(done * 100 / Math.Max(1, total));
                if (pct >= lastPercent + 5)
                {
                    lastPercent = pct;
                    store.Append(state, JournalKind.Progress, what, percent: pct);
                }
            }, CancellationToken.None);
            state.BasePartitionId = result.PartitionId;
            state.SourceUsedBytes = result.UsedBytes;
            state.CopiedBytes = result.Copy.CopiedBytes;
            store.Append(state, JournalKind.Install, $"Základná vrstva: {Format.Gb(result.Copy.CopiedBytes)} skopírovaných, SHA-256 {result.Copy.Sha256[..16]}…, overené.");
            store.Save(state);

            Log("Pripravujem kópiu Windows na štart z vrstvy…");
            var labFile = Path.Combine(store.Root, "Program", "polaris-lab.exe");
            OfflineImage.CopyClean(Environment.ProcessPath!, labFile);
            using (var image = BaseBuilder.Mount(basePath, Path.Combine(store.Root, "Pracovne", "zaklad")))
            {
                var changes = OfflineImage.Prepare(image.Folder, result.PartitionId, "\"C:\\Program Files\\POLARIS\\polaris-lab.exe\" sluzba");
                OfflineImage.InstallFiles(image.Folder, labFile);
                store.Append(state, JournalKind.Install,
                    $"Kópia upravená: C: patrí vrstve (predtým {changes.MountedDevicesBefore}), {changes.ControlSet}, služba zaregistrovaná.");
            }

            File.SetAttributes(basePath, File.GetAttributes(basePath) | FileAttributes.ReadOnly);
            state.BaseFileBytes = new FileInfo(basePath).Length;
            Log($"Základná vrstva má {Format.Gb(state.BaseFileBytes)}.");

            foreach (var slot in Slots.All)
            {
                VirtualDisks.CreateDifferencing(store.SlotPath(slot), basePath);
            }

            Log("Vytvorené tri čisté vrstvy A, B, C.");

            var bcd = BcdStore.ThisSystem;
            bcd.Log = text => notes.Add(text);
            bcd.Export(Path.Combine(store.Root, "Zalohy", $"bcd-pred-instalaciou-{DateTime.Now:yyyyMMdd-HHmmss}"));
            foreach (var slot in Slots.All)
            {
                var id = bcd.Copy(facts.CurrentBoot, Slots.Description(slot));
                created.Add(id);
                bcd.SetVhd(id, SystemDrive, Store.BootPath(Slots.FileName(slot)));
                bcd.Set(id, "recoveryenabled", "No");
                bcd.Set(id, "bootstatuspolicy", "IgnoreAllFailures");
                state.Get(slot).BcdId = id;
            }

            Rotation.ResetAll(state, Slot.A, DateTimeOffset.UtcNow);
            state.Phase = LabPhase.FirstBootPending;
            state.CopySeconds = (long)clock.Elapsed.TotalSeconds;
            store.Save(state);
            bcd.SetBootSequence(state.Get(Slot.A).BcdId);
            store.Append(state, JournalKind.Install, $"Prevod hotový za {Format.Duration(clock.Elapsed)}. Ďalší štart: vrstva A (jednorazovo; pri zlyhaní sa vráti pôvodný Windows).");
            store.Save(state);
            PlaceShortcut(labFile);
            Ui.Ok($"Prevod hotový za {Format.Duration(clock.Elapsed)}.");
            return true;
        }
        catch (Exception e)
        {
            Ui.Error("PREVOD ZASTAVENÝ: " + e.Message);
            notes.Add("CHYBA: " + e);
            RollBack(store, created, createdRoot, notes);
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"POLARIS-E0-chyba-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllLines(file, notes, Serialization.Utf8);
            Ui.Warn("Pôvodný Windows ostal nedotknutý. Záznam chyby: " + file);
            return false;
        }
    }

    private static void RollBack(Store store, List<Guid> created, bool createdRoot, List<string> notes)
    {
        foreach (var id in created)
        {
            try
            {
                BcdStore.ThisSystem.Delete(id);
            }
            catch (Exception e) when (e is InvalidOperationException or SystemCallException)
            {
                notes.Add("Návrat: záznam štartu " + id + ": " + e.Message);
            }
        }

        try
        {
            BcdStore.ThisSystem.ClearBootSequence();
        }
        catch (Exception e) when (e is InvalidOperationException or SystemCallException)
        {
            notes.Add("Návrat: jednorazový štart: " + e.Message);
        }

        if (createdRoot)
        {
            Volumes.Unmount(Path.Combine(store.Root, "Pracovne", "zaklad"));
            Maintenance.DeleteTree(store.Root, notes);
        }
    }

    /// <summary>A shortcut on the common desktop of the original Windows.</summary>
    public static void PlaceShortcut(string labFile)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        File.WriteAllText(Path.Combine(desktop, "POLARIS laboratorium.cmd"), $"@echo off\r\nstart \"\" \"{labFile}\"\r\n", Serialization.Utf8);
    }
}
