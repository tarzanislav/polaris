using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Polaris.Agent;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>K6a on a real Windows (the CI runner as an administrator): programs starting by themselves switched as Task
/// Manager does, the request that the PC must not sleep, the session and memory as Windows says them, the icon's
/// "ready" over the pipe. Elsewhere they pass without doing anything; all test entries are removed.</summary>
public static class WindowsComfortChecks
{
    private const string TestRoot = @"SOFTWARE\PolarisSkuska";
    private const string Entry = "PolarisSkuskaSpustania";

    [Check]
    public static void StartupIsSwitchedAsTaskManagerDoesAndPutBack()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        Run(@"""C:\Windows\System32\notepad.exe"" /skuska");
    }

    [SupportedOSPlatform("windows")]
    private static void Run(string command)
    {
        var root = $@"{TestRoot}\{Guid.NewGuid():N}";
        var memory = new StartupMemory(root + @"\Spustanie");
        var handler = new StartupHandler(memory, root);
        var file = Path.Combine(StartupEntries.CommonFolder, Entry + ".cmd");
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var run = hklm.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
        {
            run.SetValue(Entry, command, RegistryValueKind.String);
        }

        Directory.CreateDirectory(StartupEntries.CommonFolder);
        File.WriteAllText(file, "@echo off\r\n");
        try
        {
            // everything already on the runner stays allowed – only the two test entries are switched
            var others = StartupEntries.Read().Where(e => !e.Name.StartsWith(Entry, StringComparison.Ordinal)).Select(e => e.Name.TrimEnd('*')).Distinct().ToList();
            var element = new StartupElement { Id = "s", Allow = others };
            var seen = handler.Observe(element);
            Expect.True(seen.State != Observed.Present && seen.Detail.Contains(Entry, StringComparison.Ordinal), "skúšobné položky sa spúšťajú: " + seen.Detail);

            handler.Apply(element);
            Expect.True(!StartupRules.IsOn(StartupEntries.Approval(StartupEntries.Run, Entry)), "Run: vypnuté značkou Správcu úloh");
            Expect.True(!StartupRules.IsOn(StartupEntries.Approval(StartupEntries.Folder, Entry + ".cmd")), "priečinok Po spustení: vypnuté");
            Expect.True(StartupEntries.Read().Single(e => e.Place == StartupEntries.Run && e.Name == Entry) is { On: false } e && e.Command == command, "položka sama ostala");
            Expect.Equal(2, memory.All().Count, "POLARIS si pamätá pôvodný stav oboch");
            Expect.Sequence(others, StartupRule.Read(root) ?? [], "služba stráži zoznam");
            Expect.Equal(Observed.Present, handler.Observe(element).State, "po zmene je PC podľa prvku");

            element.Allow = [.. others, "notepad"];
            var back = handler.Observe(element);
            Expect.True(back.State == Observed.Different && back.Detail.Contains("znova zapnúť", StringComparison.Ordinal), "povolený program sa vráti: " + back.Detail);
            handler.Apply(element);
            Expect.True(StartupEntries.Approval(StartupEntries.Run, Entry) is null, "pôvodne bez značky – značka zmizla");
            Expect.Equal(1, memory.All().Count, "zabudnutý");

            element.Present = false;
            Expect.Equal(Observed.Present, handler.Observe(element).State, "bez prvku sa má vrátiť, čo POLARIS vypol");
            handler.Apply(element);
            Expect.True(StartupRules.IsOn(StartupEntries.Approval(StartupEntries.Folder, Entry + ".cmd")), "aj priečinok znova zapnutý");
            Expect.True(memory.All().Count == 0 && StartupRule.Read(root) is null, "nič si nepamätá, nič nestráži");
            Expect.Equal(Observed.Absent, handler.Observe(element).State, "hotovo");
        }
        finally
        {
            using (var run = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                run?.DeleteValue(Entry, throwOnMissingValue: false);
            }

            StartupEntries.SetApproval(StartupEntries.Run, Entry, null);
            StartupEntries.SetApproval(StartupEntries.Folder, Entry + ".cmd", null);
            File.Delete(file);
            Registry.LocalMachine.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Check]
    public static void ThePcStaysAwakeWhileAsked()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var reason = "POLARIS test " + Guid.NewGuid().ToString("N")[..8]; // ASCII: powercfg writes in the code page of the console
        using (new Polaris.Windows.AwakeRequest(reason))
        {
            Expect.True(Requests().Contains(reason, StringComparison.Ordinal), "powercfg /requests ukáže požiadavku s dôvodom");
        }

        Expect.True(!Requests().Contains(reason, StringComparison.Ordinal), "po skončení je preč");

        var before = MaintenanceStore.Until();
        try
        {
            var until = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
            MaintenanceStore.Set(until);
            Expect.Equal(until, MaintenanceStore.Until(), "koniec údržby v registri");
            MaintenanceStore.Set(null);
            Expect.True(MaintenanceStore.Until() is null, "zrušená");
        }
        finally
        {
            MaintenanceStore.Set(before);
        }
    }

    [Check]
    public static void SessionAndMemoryAsWindowsSaysThem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var session = Process.GetCurrentProcess().SessionId;
        var answer = Polaris.Windows.Logons.Query(session);
        Expect.True(answer is not null, $"relácia {session}: Windows odpovedá");
        var now = DateTimeOffset.FromFileTime(answer!.CurrentFileTime).ToUniversalTime();
        Expect.True(Math.Abs((now - DateTimeOffset.UtcNow).TotalMinutes) < 2, $"čas Windows v odpovedi je teraz ({now:O}) – štruktúra sa číta správne");
        if (Polaris.Windows.Logons.Of(session) is { } logon)
        {
            Expect.True(logon.LogonUtc <= DateTimeOffset.UtcNow && logon.Account.Length > 0, "prihlásenie v minulosti, s menom konta");
        }

        var (total, available) = Polaris.Windows.MemoryStatus.Physical();
        Expect.True(total > (512UL << 20) && available > 0 && available <= total, "pamäť PC");
    }

    [Check]
    public static void TheIconSaysReadyAndWindowsSaysItsSession()
    {
        if (!OperatingSystem.IsWindows() || !IsAdmin())
        {
            return;
        }

        var pipe = "polaris-obchod-skuska-" + Guid.NewGuid().ToString("N")[..8];
        using var stop = new CancellationTokenSource();
        var sessions = new List<int>();
        var work = new StoreWork(new StoreState($@"{TestRoot}\{Guid.NewGuid():N}"), new NoPrograms(), _ => { }, () => DateTimeOffset.UtcNow, _ => true);
        var server = new Thread(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                StorePipe.Serve(work, _ => { }, stop.Token, pipe, ready: s =>
                {
                    lock (sessions)
                    {
                        sessions.Add(s);
                    }
                });
            }
        })
        { IsBackground = true };
        server.Start();
        try
        {
            var answer = StoreClient.Ask(new StoreRequest { Command = StoreClient.ReadyCommand }, TimeSpan.FromSeconds(30), pipe);
            Expect.True(answer.Ok, "služba prijala: " + answer.Message);
            lock (sessions)
            {
                Expect.Sequence([Process.GetCurrentProcess().SessionId], sessions, "relácia z Windows, nie zo žiadosti");
            }
        }
        finally
        {
            stop.Cancel();
            server.Join(TimeSpan.FromSeconds(10));
        }
    }

    [SupportedOSPlatform("windows")]
    private static string Requests()
    {
        using var p = Process.Start(new ProcessStartInfo("powercfg", "/requests") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text;
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

    private sealed class NoPrograms : IElementHandler
    {
        public bool CanHandle(Element element) => element is ProgramElement;

        public Observation Observe(Element element) => new(Observed.Absent);

        public void Apply(Element element) => throw new InvalidOperationException("skúška nič neinštaluje");
    }
}
