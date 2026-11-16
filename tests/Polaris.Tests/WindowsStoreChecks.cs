using System.Security.Principal;
using Microsoft.Win32;
using Polaris.Agent;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>The School store and the Sandbox templates on a real Windows: registry, files and the named pipe with the
/// identity of the caller from Windows. Only on Windows with the rights of an administrator (the CI runner).</summary>
public static class WindowsStoreChecks
{
    private static readonly DateTimeOffset T = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Programs that exist only in memory – the store's logic without installing anything real.</summary>
    private sealed class FakePrograms : IElementHandler
    {
        public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FailRemoval { get; set; }

        public bool CanHandle(Element element) => element is ProgramElement;

        public Observation Observe(Element element) =>
            Installed.Contains(((ProgramElement)element).DisplayName) ? new(Observed.Present, "je") : new(Observed.Absent, "nie je");

        public void Apply(Element element)
        {
            var p = (ProgramElement)element;
            if (p.Present)
            {
                Installed.Add(p.DisplayName);
            }
            else if (FailRemoval)
            {
                throw new InvalidOperationException("odinštalátor zlyhal");
            }
            else
            {
                Installed.Remove(p.DisplayName);
            }
        }
    }

    private static StoreElement Catalogue(params string[] accounts) => new()
    {
        Id = "obchod",
        Accounts = [.. accounts],
        MaxPerSession = 2,
        Items =
        [
            new() { Id = "a", Title = "Alfa", Program = new() { Id = "a", Winget = "V.Alfa", DisplayName = "Alfa" } },
            new() { Id = "b", Title = "Beta", Program = new() { Id = "b", Winget = "V.Beta", DisplayName = "Beta" } },
            new() { Id = "c", Title = "Gama", Program = new() { Id = "c", Winget = "V.Gama", DisplayName = "Gama" } },
        ],
    };

    [Check]
    public static void SandboxTemplatesAreFilesOnTheDesktop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("polaris-plocha-").FullName;
        try
        {
            var handler = new SandboxHandler(dir);
            var s = new SandboxElement { Id = "s", Name = "Pokusy" };
            var file = Path.Combine(dir, "Pieskovisko – Pokusy.wsb");
            Expect.Equal(Observed.Absent, handler.Observe(s).State, "na začiatku nie je");
            handler.Apply(s);
            Expect.True(File.Exists(file), "súbor na ploche");
            Expect.Equal(Observed.Present, handler.Observe(s).State, "po zápise");
            Expect.True(File.ReadAllBytes(file)[0] == (byte)'<', "bez značky BOM");
            Expect.Equal(Observed.Different, handler.Observe(new SandboxElement { Id = "s", Name = "Pokusy", Networking = true }).State, "iné nastavenia");
            s.Present = false;
            handler.Apply(s);
            Expect.True(!File.Exists(file), "odstránené");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void TheStoreInstallsCountsAndRemovesAfterTheSession()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var key = $@"SOFTWARE\PolarisSkuska\{Guid.NewGuid():N}";
        try
        {
            var state = new StoreState(key);
            var handler = new StoreHandler(state);
            var element = Catalogue("ziak");
            Expect.Equal(Observed.Absent, handler.Observe(element).State, "obchod zatvorený");
            handler.Apply(element);
            Expect.Equal(Observed.Present, handler.Observe(element).State, "katalóg zapísaný");
            Expect.Equal(3, state.Catalogue()!.Items.Count, "katalóg sa dá prečítať");
            var changed = Catalogue("ziak", "ziak2");
            Expect.Equal(Observed.Different, handler.Observe(changed).State, "iný katalóg");

            var programs = new FakePrograms();
            programs.Installed.Add("Gama"); // on the PC permanently, from a recipe
            var journal = new List<RunRecord>();
            var cleaned = true;
            var work = new StoreWork(state, programs, journal.Add, () => T, _ => cleaned);
            StoreAnswer Install(string id, string account = "ziak", bool admin = false) => work.Handle(new StoreRequest { Command = "install", Item = id }, account, admin);

            var list = work.Handle(new StoreRequest { Command = "list" }, "ziak", false);
            Expect.True(list.Ok && list.Items.Count == 3 && list.Items.Single(i => i.Id == "c").Installed, "zoznam, Gama už je");
            Expect.True(Install("a").Ok && programs.Installed.Contains("Alfa"), "Alfa nainštalovaná");
            Expect.True(Install("a").Message.Contains("už nainštalovaný", StringComparison.Ordinal), "druhýkrát nie");
            Expect.True(Install("c").Message.Contains("natrvalo", StringComparison.Ordinal), "program z receptu sa nezaznamená");
            Expect.True(Install("a", "ucitel").Message.Contains("nemôže", StringComparison.Ordinal), "cudzie konto");
            Expect.True(Install("b", admin: true).Message.Contains("Správca", StringComparison.Ordinal), "správca");
            cleaned = false;
            Expect.True(Install("b").Message.Contains("nečistí", StringComparison.Ordinal), "bez čistenia relácie nič");
            cleaned = true;
            Expect.True(Install("b").Ok, "Beta");
            Expect.Equal(2, state.Installed().Count, "dva záznamy");
            Expect.Equal(2, journal.Count, "dva zápisy do denníka");
            Expect.True(work.Handle(new StoreRequest { Command = "nieco" }, "ziak", false).Message.Contains("Neznámy", StringComparison.Ordinal), "neznámy príkaz");
            Expect.True(!work.Handle(null, "ziak", false).Ok, "pokazená požiadavka");

            Expect.Equal(null, work.RemoveFor("ucitel"), "cudzie konto nemá čo odstrániť");
            programs.FailRemoval = true;
            Expect.True(work.RemoveFor("ziak")!.Contains("skúsi sa znova", StringComparison.Ordinal), "zlyhanie sa ohlási");
            Expect.Equal(2, state.Installed().Count, "záznamy ostávajú na ďalší pokus");
            programs.FailRemoval = false;
            Expect.True(work.RemoveFor("ziak")!.Contains("odstránené: 2", StringComparison.Ordinal), "odstránené");
            Expect.True(state.Installed().Count == 0 && !programs.Installed.Contains("Alfa") && programs.Installed.Contains("Gama"), "program z receptu ostal");

            element.Present = false;
            handler.Apply(element);
            Expect.Equal(null, state.Catalogue(), "obchod zatvorený");
        }
        finally
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            root.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }

    [Check]
    public static void ThePipeKnowsWhoAsks()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var key = $@"SOFTWARE\PolarisSkuska\{Guid.NewGuid():N}";
        var pipe = "polaris-obchod-skuska-" + Guid.NewGuid().ToString("N")[..8];
        using var stop = new CancellationTokenSource();
        var logged = new List<string>();
        var state = new StoreState(key);
        var programs = new FakePrograms();
        var work = new StoreWork(state, programs, _ => { }, () => T, _ => true);
        var server = new Thread(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                StorePipe.Serve(work, logged.Add, stop.Token, pipe);
            }
        })
        { IsBackground = true };
        server.Start();
        try
        {
            var limit = TimeSpan.FromSeconds(30);
            var closed = StorePipe.Ask(new StoreRequest { Command = "list" }, limit, pipe);
            Expect.True(!closed.Ok && closed.Message.Contains("zatvorený", StringComparison.Ordinal), "bez katalógu: " + closed.Message);

            state.SetCatalogue(Catalogue(Environment.UserName));
            var list = StorePipe.Ask(new StoreRequest { Command = "list" }, limit, pipe);
            Expect.True(list.Ok && list.Items.Count == 3, "zoznam cez rúru: " + list.Message);

            // the request cannot say who asks – the runner's account is an administrator and Windows says so
            var refused = StorePipe.Ask(new StoreRequest { Command = "install", Item = "a" }, limit, pipe);
            Expect.True(!refused.Ok && refused.Message.Contains("Správca", StringComparison.Ordinal), "správca odmietnutý: " + refused.Message);
            Expect.True(programs.Installed.Count == 0, "nič sa nenainštalovalo");
            Expect.Sequence(Array.Empty<string>(), logged, "rúra bez chýb");
        }
        finally
        {
            stop.Cancel();
            server.Join(TimeSpan.FromSeconds(10));
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            root.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
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
