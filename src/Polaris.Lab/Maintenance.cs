using Polaris.Core;
using Polaris.Windows;

namespace Polaris.Lab;

/// <summary>Actions of the owner besides the conversion: continue in layers, the test of restarts, the mode, removing.</summary>
internal static class Maintenance
{
    /// <summary>From the original Windows: all layers clean again and the next start into layer A.</summary>
    public static void Resume(Store store)
    {
        store.Update(state =>
        {
            var basePath = Path.Combine(store.Directory, state.BaseFile);
            foreach (var slot in Slots.All)
            {
                var path = store.SlotPath(slot);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                VirtualDisks.CreateDifferencing(path, basePath);
            }

            Rotation.ResetAll(state, Slot.A, DateTimeOffset.UtcNow);
            if (state.Phase == LabPhase.Running && state.AutoTest.Active)
            {
                state.AutoTest.Stop("PC sa vrátilo do pôvodného Windows");
            }

            var bcd = BcdStore.ThisSystem;
            if (bcd.ReadBootManager().Default != state.OriginalBcdId)
            {
                bcd.SetDefault(state.OriginalBcdId);
            }

            bcd.SetBootSequence(state.Get(Slot.A).BcdId);
            state.LastRestartRequestUtc = DateTimeOffset.UtcNow;
            store.Append(state, JournalKind.Note, "Pokračovanie z pôvodného Windows: všetky vrstvy čisté, ďalší štart vrstva A.");
        });
    }

    public static void StartAutoTest(Store store, int count, int waitSeconds)
    {
        store.Update(state =>
        {
            state.AutoTest = new AutoTestState
            {
                Active = true,
                Total = count,
                Remaining = count - 1, // the first restart is the one asked for right now
                WaitSeconds = waitSeconds,
                StartedUtc = DateTimeOffset.UtcNow,
            };
            state.LastRestartRequestUtc = DateTimeOffset.UtcNow;
            store.Append(state, JournalKind.AutoTest, $"Skúška reštartov: {count} štartov, čakanie {waitSeconds} s pri každom.");
        });
    }

    public static void StopAutoTest(Store store) => store.Update(state =>
    {
        if (state.AutoTest.Active)
        {
            state.AutoTest.Stop("zastavená ručne");
            store.Append(state, JournalKind.AutoTest, "Skúška zastavená ručne.");
        }
    });

    public static void SwitchMode(Store store, Slot running)
    {
        store.Update(state =>
        {
            state.Mode = state.Mode == LabMode.Safe ? LabMode.Live : LabMode.Safe;
            store.Append(state, JournalKind.Mode, "Režim: " + Report.ModeText(state.Mode));
        });
        Agent.Rotate(AgentEnvironment.Real(store), running);
    }

    public static void RestartRequested(Store store) => store.Update(state =>
    {
        state.LastRestartRequestUtc = DateTimeOffset.UtcNow;
        store.Append(state, JournalKind.Note, "Reštart na žiadosť správcu.");
    });

    /// <summary>Removes POLARIS: the boot configuration as before, the layers deleted (from a layer: what can be
    /// deleted now; the rest is finished in the original Windows).</summary>
    public static bool Uninstall(Store store, Slot? running)
    {
        var notes = new List<string>();
        store.Update(state =>
        {
            var bcd = BcdStore.ThisSystem;
            bcd.ClearBootSequence();
            if (state.OriginalBoot.Default is { } d && bcd.Exists(d))
            {
                bcd.SetDefault(d);
            }
            else
            {
                bcd.SetDefault(state.OriginalBcdId);
            }

            var order = state.OriginalBoot.DisplayOrder.Where(bcd.Exists).ToList();
            if (order.Count > 0)
            {
                bcd.SetDisplayOrder(order);
            }

            if (state.OriginalBoot.Timeout is { } t)
            {
                bcd.SetTimeout(t);
            }

            foreach (var slot in state.Slots)
            {
                if (slot.Slot != running && slot.BcdId != Guid.Empty)
                {
                    bcd.Delete(slot.BcdId);
                }
            }

            state.Phase = LabPhase.Uninstalling;
            if (state.AutoTest.Active)
            {
                state.AutoTest.Stop("odinštalovanie");
            }

            store.Append(state, JournalKind.Uninstall, running is null
                ? "Odinštalovanie z pôvodného Windows."
                : $"Odinštalovanie z vrstvy {running}: ďalší štart je pôvodný Windows, súbory sa zmažú tam.");
        });

        if (running is not null)
        {
            return false;
        }

        // from the original Windows: the remaining entry (if a layer started the removal) and all files
        var last = store.Load();
        foreach (var slot in last.Slots)
        {
            if (slot.BcdId != Guid.Empty)
            {
                BcdStore.ThisSystem.Delete(slot.BcdId);
            }
        }

        DeleteTree(store.Root, notes);
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "POLARIS laboratorium.cmd");
        if (File.Exists(shortcut))
        {
            File.Delete(shortcut);
        }

        foreach (var n in notes)
        {
            Ui.Warn(n);
        }

        return notes.Count == 0;
    }

    /// <summary>Deletes a folder with everything in it, read-only files too.</summary>
    public static void DeleteTree(string path, List<string> notes)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                notes.Add("Súbor " + file + ": " + e.Message);
            }
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notes.Add("Priečinok " + path + " sa nedal celý zmazať: " + e.Message);
        }
    }
}
