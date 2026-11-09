using Microsoft.Win32;
using Polaris.Core;
using Polaris.Windows;

namespace Polaris.Lab;

/// <summary>Where the agent works: the real system, or a test bench in CI (a store file of the boot configuration,
/// a chosen layer, no restarts).</summary>
internal sealed record AgentEnvironment(
    Store Store,
    BcdStore Bcd,
    Guid BootIdentifier,
    string MarkerFile,
    string MarkerRegistryKey,
    bool MayRestart)
{
    public static AgentEnvironment Real(Store store) => new(
        store,
        BcdStore.ThisSystem,
        SystemInfo.BootIdentifier(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "POLARIS-TEST-ZMIZNE.txt"),
        @"SOFTWARE\POLARIS-Test",
        MayRestart: true);
}

/// <summary>The agent: runs as a service in every layer. At each start it counts the start, checks that nothing of the
/// previous session survived, makes the used layers clean again, points the next start to a clean layer and – during
/// the test of restarts – restarts the PC by itself.</summary>
internal static class Agent
{
    public static void Service(CancellationToken stop)
    {
        var store = Store.Find();
        if (store is null)
        {
            return;
        }

        var env = AgentEnvironment.Real(store);
        var outcome = AtStart(env);
        if (outcome is null)
        {
            return;
        }

        WriteDesktopStatus(store);
        if (outcome.Value.RestartNow)
        {
            SystemInfo.Restart("POLARIS: vrstva nebola čistá – štartujem čistú vrstvu.", force: true);
            return;
        }

        WatchBootTime(store, outcome.Value.Slot, stop);
        if (outcome.Value.AutoTest)
        {
            AutoTestStep(store, stop);
        }

        stop.WaitHandle.WaitOne();
    }

    public readonly record struct StartOutcome(Slot Slot, BootRecord Record, bool AutoTest, bool RestartNow);

    /// <summary>Everything that has to happen right after a start. Null when Windows does not run from a POLARIS layer.</summary>
    public static StartOutcome? AtStart(AgentEnvironment env)
    {
        var store = env.Store;
        var kernelToAgent = Environment.TickCount64;
        var markerFile = File.Exists(env.MarkerFile);
        bool markerRegistry;
        using (var k = Registry.LocalMachine.OpenSubKey(env.MarkerRegistryKey))
        {
            markerRegistry = k is not null;
        }

        StartOutcome? outcome = null;
        store.Update(state =>
        {
            if (Rotation.RunningSlot(state, env.BootIdentifier) is not { } running)
            {
                store.Append(state, JournalKind.Note, "Agent beží mimo vrstiev POLARIS – nič nerobí.");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var expected = Rotation.IsExpected(state, running);
            var boots = Rotation.RecordBoot(state, running, now);
            long? cycle = state.LastRestartRequestUtc is { } asked ? (long)(now - asked).TotalMilliseconds : null;
            state.LastRestartRequestUtc = null;
            var auto = state.AutoTest.Active;
            var record = new BootRecord
            {
                Slot = running,
                SlotBoots = boots,
                Expected = expected,
                MarkerFileFound = markerFile,
                MarkerRegistryFound = markerRegistry,
                KernelToAgentMs = kernelToAgent,
                CycleMs = cycle,
                AutoTest = auto,
                AutoTestIndex = auto ? state.AutoTest.CurrentIndex : null,
            };
            var how = cycle is null ? "bez žiadosti POLARIS (ručný reštart, vypnutie alebo výpadok)" : $"cyklus {Format.Seconds(cycle.Value / 1000.0)}";
            store.Append(state, JournalKind.Boot,
                $"Štart z vrstvy {running}: {(record.Clean ? "čistá" : "NEČISTÁ – " + Report.Why(record))}, {(expected ? "podľa prípravy" : "MIMO PRÍPRAVY")}, {how}"
                + (auto ? $", skúška {record.AutoTestIndex}/{state.AutoTest.Total}" : ""),
                running,
                record);
            var dirty = !record.Clean || !expected;
            if (dirty)
            {
                store.Append(state, JournalKind.Alarm, $"Vrstva {running}: {Report.Why(record)}" + (expected ? "" : "; štart mimo prípravy"), running);
                if (state.AutoTest.Active)
                {
                    state.AutoTest.Stop($"nečistý štart vrstvy {running}");
                    store.Append(state, JournalKind.AutoTest, "Skúška zastavená: nečistý štart.", running);
                }
            }
            else if (auto && state.AutoTest.Remaining == 0)
            {
                state.AutoTest.Active = false;
                store.Append(state, JournalKind.AutoTest, $"Skúška dokončená: {state.AutoTest.Total} štartov.", running);
            }

            // a layer that was used is never worked in: prepare clean ones and restart at once
            var used = boots > 1;
            outcome = new StartOutcome(running, record, state.AutoTest.Active, used);
        });

        if (outcome is not { } o)
        {
            return null;
        }

        Rotate(env, o.Slot);
        LeaveMarkers(env, o.Slot);
        return o;
    }

    /// <summary>Makes the used layers clean and points the starts – in the order that keeps the boot manager away from a
    /// used layer as long as possible: first the default (the third layer), then the one-time next start.</summary>
    public static void Rotate(AgentEnvironment env, Slot running)
    {
        var store = env.Store;
        store.Update(state =>
        {
            var plan = Rotation.Plan(state, running, s => File.Exists(store.SlotPath(s)));
            var basePath = Path.Combine(store.Directory, state.BaseFile);
            if (plan.Recreate.Contains(plan.Third))
            {
                Recreate(store, plan.Third, basePath);
                Rotation.MarkRecreated(state, plan.Third, DateTimeOffset.UtcNow);
            }

            var current = env.Bcd.ReadBootManager();
            if (current.Default != plan.DefaultTarget)
            {
                env.Bcd.SetDefault(plan.DefaultTarget);
            }

            if (!current.DisplayOrder.SequenceEqual(plan.DisplayOrder))
            {
                env.Bcd.SetDisplayOrder(plan.DisplayOrder);
            }

            if (current.Timeout != 3)
            {
                env.Bcd.SetTimeout(3);
            }

            if (plan.Recreate.Contains(plan.Next))
            {
                Recreate(store, plan.Next, basePath);
                Rotation.MarkRecreated(state, plan.Next, DateTimeOffset.UtcNow);
            }

            env.Bcd.SetBootSequence(state.Get(plan.Next).BcdId);
            Rotation.MarkApplied(state, plan);
            var fresh = plan.Recreate.Count == 0 ? "žiadna" : string.Join(", ", plan.Recreate);
            var fallback = plan.DefaultTarget == state.OriginalBcdId ? "pôvodný Windows" : "vrstva " + plan.Third;
            store.Append(state, JournalKind.Rotation, $"Pripravené: ďalší štart vrstva {plan.Next}, po výpadku {fallback}; nanovo vytvorené: {fresh}.", running);
        });
    }

    private static void Recreate(Store store, Slot slot, string basePath)
    {
        var path = store.SlotPath(slot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        VirtualDisks.CreateDifferencing(path, basePath);
    }

    /// <summary>Leaves traces of this session; the next start must not find them.</summary>
    private static void LeaveMarkers(AgentEnvironment env, Slot slot)
    {
        var stamp = $"vrstva {slot}, {Format.Local(DateTimeOffset.UtcNow)}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(env.MarkerFile)!);
            File.WriteAllText(env.MarkerFile,
                "Tento súbor vytvoril POLARIS pri štarte (" + stamp + ").\r\nPo reštarte musí zmiznúť – tak sa overuje zmrazenie.\r\n",
                Serialization.Utf8);
            using var k = Registry.LocalMachine.CreateSubKey(env.MarkerRegistryKey);
            k.SetValue("Relacia", stamp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            env.Store.Update(s => env.Store.Append(s, JournalKind.Error, "Skúšobnú stopu sa nepodarilo zapísať: " + e.Message, slot));
        }
    }

    /// <summary>Waits (at most 6 minutes) for Windows' own measurement of this start and writes it to the journal.</summary>
    private static void WatchBootTime(Store store, Slot slot, CancellationToken stop)
    {
        var bootStart = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        _ = Task.Run(() =>
        {
            var until = DateTime.UtcNow.AddMinutes(6);
            while (DateTime.UtcNow < until && !stop.IsCancellationRequested)
            {
                try
                {
                    var found = BootEvents.Recent(3).FirstOrDefault(b => b.Created > bootStart);
                    if (found is not null)
                    {
                        store.Update(s => store.Append(s, JournalKind.BootTime, $"Windows nameral štart: {Format.Seconds(found.Milliseconds / 1000.0)}.", slot, windowsBootMs: found.Milliseconds));
                        return;
                    }
                }
                catch (Exception e) when (e is IOException or System.Xml.XmlException or SystemCallException)
                {
                    // try again in a while
                }

                stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
            }
        }, stop);
    }

    /// <summary>One step of the test of restarts: wait, then restart.</summary>
    private static void AutoTestStep(Store store, CancellationToken stop)
    {
        var wait = store.Load().AutoTest.WaitSeconds;
        if (stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Clamp(wait, 30, 3600))))
        {
            return;
        }

        var go = store.Update(state =>
        {
            if (!state.AutoTest.Active || state.AutoTest.Remaining <= 0)
            {
                return false;
            }

            state.AutoTest.Remaining--;
            state.LastRestartRequestUtc = DateTimeOffset.UtcNow;
            store.Append(state, JournalKind.AutoTest, $"Reštart skúšky ({state.AutoTest.CurrentIndex + 1}/{state.AutoTest.Total}).");
            return true;
        });
        if (go)
        {
            store.WriteReport(store.Load());
            SystemInfo.Restart("POLARIS: skúška reštartov", force: true);
        }
    }

    /// <summary>A short note on the common desktop of the layer (it disappears with the layer, a new one is written at
    /// every start).</summary>
    public static void WriteDesktopStatus(Store store)
    {
        try
        {
            var state = store.Load();
            var report = store.WriteReport(state);
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "POLARIS-stav.txt");
            var gate = Report.Gate(store.Journal());
            File.WriteAllText(file,
                $"{Product.Name} {Product.Stage}: zmrazené – zmeny sa po reštarte zahodia.\r\n"
                + $"Režim: {Report.ModeText(state.Mode)}\r\n"
                + $"Skúška: {(state.AutoTest.Active ? $"prebieha ({state.AutoTest.CurrentIndex}/{state.AutoTest.Total})" : "neprebieha")}\r\n"
                + $"Čisté štarty skúšky: {gate.CleanAutoTestBoots} z {gate.Required}\r\n"
                + $"Celá správa: {report}\r\n",
                Serialization.Utf8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // only a convenience
        }
    }
}
