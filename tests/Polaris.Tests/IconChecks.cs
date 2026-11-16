using Polaris.Agent;
using Polaris.Recipes;
using Polaris.Student;

namespace Polaris.Tests;

/// <summary>The pupil's icon: the drawn star, the menu, and the watch that starts a fallen icon again.</summary>
public static class IconChecks
{
    private static readonly DateTimeOffset T = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);

    [Check]
    public static void TheStarIsDrawnSmoothAndSymmetric()
    {
        foreach (var size in new[] { 16, 20, 24, 32, 48, 64 })
        {
            var p = StarIcon.Pixels(size);
            uint At(int x, int y) => p[(y * size) + x];
            Expect.Equal(0u, At(0, 0) >> 24, $"{size}: roh je priehľadný");
            Expect.Equal(0xFF000000u | StarIcon.Light, At(size / 2, size / 2), $"{size}: stred je hviezda");
            Expect.Equal(0xFFu, At(size / 2, (size / 8) + 1) >> 24, $"{size}: horný lúč je nepriehľadný");
            Expect.True(p.Any(x => x >> 24 is > 0 and < 255), $"{size}: okraj je vyhladený");
            Expect.True(p.Count(x => (x & 0xFFFFFF) == StarIcon.Sky && x >> 24 == 255) > size, $"{size}: obloha okolo hviezdy");
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (At(x, y) != At(size - 1 - x, y) || At(x, y) != At(x, size - 1 - y))
                    {
                        throw new CheckFailed($"{size}: hviezda nie je súmerná na {x},{y}");
                    }
                }
            }
        }
    }

    [Check]
    public static void TheMenuShowsTheStoreAndTheSession()
    {
        var store = new StoreAnswer
        {
            Ok = true,
            Items = [new() { Id = "a", Title = "Audacity", Description = "zvuk", Installed = true }, new() { Id = "i", Title = "Inkscape" }, new() { Id = "g", Title = "GIMP" }],
        };
        var menu = TrayMenu.Build(store, ["g"], ["Pokusy", "Web"], SessionCleaning.AtSignOut);
        var shop = menu[0].Children!;
        Expect.True(shop[0].Checked && !shop[0].Enabled && shop[0].Text == "Audacity – zvuk", "nainštalovaný: ✓ a sivý");
        Expect.True(shop[1].Enabled && shop[1].Command == TrayMenu.StoreFirst + 1, "dá sa nainštalovať");
        Expect.True(!shop[2].Enabled && shop[2].Text.EndsWith("(inštaluje sa…)", StringComparison.Ordinal), "práve sa inštaluje");
        Expect.True(shop[^1].Command == TrayMenu.Refresh && shop[^2].IsSeparator, "obnoviť ponuku");
        Expect.Equal(TrayMenu.SandboxFirst + 1, menu[1].Children![1].Command, "druhá šablóna Pieskoviska");
        Expect.True(!menu[^1].Enabled && menu[^1].Text.Contains("po odhlásení", StringComparison.OrdinalIgnoreCase), "upozornenie na čistenie");

        var closed = TrayMenu.Build(new StoreAnswer { Ok = false, Message = "Školský obchod je na tomto PC zatvorený." }, [], [], null);
        Expect.True(closed.Count == 1 && !closed[0].Children![0].Enabled && closed[0].Children![0].Text.Contains("zatvorený", StringComparison.Ordinal), "zatvorený obchod, žiadne Pieskovisko ani čistenie");
        Expect.True(TrayMenu.Build(null, [], [], null)[0].Children![0].Text.StartsWith("Načítavam", StringComparison.Ordinal), "ponuka sa ešte načítava");

        foreach (var mode in Enum.GetValues<SessionCleaning>())
        {
            Expect.True(TrayMenu.Tooltip(mode).Length < 128, "popis ikony sa zmestí");
            Expect.True((TrayMenu.Notice(mode) is null) == (mode == SessionCleaning.Never), "oznámenie pri prihlásení: " + mode);
            Expect.True(TrayMenu.Notice(mode) is not { } n || n.Length < 256, "oznámenie sa zmestí");
        }

        var found = TrayMenu.Sandboxes([@"C:\P\Pieskovisko – Web.wsb", @"C:\P\Pieskovisko – Abeceda.wsb", @"C:\P\iny.wsb", @"C:\P\Pieskovisko – x.txt"]);
        Expect.Sequence(new[] { "Abeceda", "Web" }, found.Select(f => f.Name), "šablóny z plochy podľa mena");
    }

    private sealed class FakeIcon : IIconProcess
    {
        public bool HasExited { get; set; }

        public bool Stopped { get; private set; }

        public bool Disposed { get; private set; }

        public void Stop() => Stopped = HasExited = true;

        public void Dispose() => Disposed = true;
    }

    [Check]
    public static void AFallenIconIsStartedAgain()
    {
        var started = new List<(int Session, FakeIcon Icon)>();
        var notes = new List<string>();
        var fail = false;
        var watch = new IconWatch(
            session =>
            {
                if (fail)
                {
                    throw new InvalidOperationException("token nie je");
                }

                var icon = new FakeIcon();
                started.Add((session, icon));
                return icon;
            },
            notes.Add);

        var now = T;
        watch.Check([1, 2], now);
        Expect.Sequence(new[] { 1, 2 }, started.Select(s => s.Session), "ikona v každej relácii");
        watch.Check([1, 2], now += IconWatch.Look);
        Expect.Equal(2, started.Count, "bežiaca sa nespúšťa znova");

        now += TimeSpan.FromMinutes(10);
        started[0].Icon.HasExited = true; // the pupil managed to end it, or it fell
        watch.Check([1, 2], now);
        Expect.True(started.Count == 3 && started[2].Session == 1 && started[0].Icon.Disposed, "padnutá sa spustí znova pri najbližšom pohľade");

        // falling right after every start: three quick falls, then a pause written to the journal once
        for (var i = 0; i < IconWatch.FastFailures; i++)
        {
            started[^1].Icon.HasExited = true;
            watch.Check([1, 2], now += IconWatch.Look);
        }

        var before = started.Count;
        watch.Check([1, 2], now += IconWatch.Look);
        Expect.True(started.Count == before && notes.Count == 1 && notes[0].Contains("padá", StringComparison.Ordinal), "po rýchlych pádoch pauza: " + string.Join("; ", notes));
        watch.Check([1, 2], now += IconWatch.Pause);
        Expect.Equal(before + 1, started.Count, "po pauze ďalší pokus");

        var two = started.Last(s => s.Session == 2).Icon;
        watch.Check([1], now += IconWatch.Look);
        Expect.True(two.Disposed, "relácia skončila, jej ikona sa uvoľní");

        fail = true;
        watch.Check([1, 3], now += IconWatch.Look);
        Expect.True(notes.Count == 2 && notes[1].Contains("nepodarilo", StringComparison.Ordinal), "chyba spustenia sa zapíše");
        watch.Check([1, 3], now += IconWatch.Look);
        Expect.Equal(2, notes.Count, "a len raz");

        watch.StopAll();
        Expect.True(started.Where(s => !s.Icon.Disposed).All(s => s.Icon.Stopped), "služba končí, ikony s ňou");
    }
}
