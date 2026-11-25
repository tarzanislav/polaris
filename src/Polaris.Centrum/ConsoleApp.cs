using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Polaris.Web;

namespace Polaris.Centrum;

/// <summary>The console as a program of this PC: a double click on POLARIS-Konzola-spravcu.exe settles it in C:\POLARIS\konzola
/// (a newer one replaces the older in place), puts it on the desktop, in Start and in Settings → Apps, and opens the
/// console in its own window (Microsoft Edge as an app window – no tabs, no address bar). The key, the centre and the
/// password are asked in that window. Closing the window ends the console.</summary>
[SupportedOSPlatform("windows")]
public static class ConsoleApp
{
    public const string ProgramName = "POLARIS-Konzola-spravcu.exe";
    private const string Title = "POLARIS konzola";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\POLARIS-konzola";

    private static string Home => Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "POLARIS", "konzola");

    private static string Own => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "POLARIS", "konzola");

    private static string Running => Path.Combine(Own, "beh.txt");

    /// <summary>The double click: install or update, then the window (without a black one when Edge is there).</summary>
    public static int Launch(TextWriter o, CancellationToken stop)
    {
        var program = Install(o);
        if (Edge() is not null)
        {
            Process.Start(new ProcessStartInfo(program, "okno") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(program)! })?.Dispose();
            return 0;
        }

        o.WriteLine("Microsoft Edge tu nie je – konzola sa otvorí v predvolenom prehliadači. Toto okno nechaj otvorené (zatvorením konzola skončí).");
        return Serve(o, stop);
    }

    /// <summary>The console behind the window: one per user; a second start only brings up the window again.</summary>
    public static int Serve(TextWriter o, CancellationToken stop)
    {
        Directory.CreateDirectory(Own);
        if (ReadRunning() is { } running)
        {
            OpenWindow(running, wait: false);
            return 0;
        }

        var folder = KeyFolder();
        using var state = new ConsoleState(folder);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var port = FreePort(Commands.ConsolePort);
        using var end = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var web = new WebServer(new IPEndPoint(IPAddress.Loopback, port), null, Commands.ConsoleRoutes(state, token, port, () => DateTimeOffset.UtcNow), _ => { }, Commands.ConsoleBody.Contains);
        var serving = web.RunAsync(end.Token);
        var address = $"http://127.0.0.1:{port}/#{token}";
        File.WriteAllText(Running, $"{Environment.ProcessId} {address}");
        try
        {
            if (Edge() is not null)
            {
                OpenWindow(address, wait: true);
                end.Cancel();
            }
            else
            {
                o.WriteLine($"Konzola: {address}");
                Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
            }

            serving.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            File.Delete(Running);
        }

        return 0;
    }

    /// <summary>Where the key and centrum.txt are: beside the program when a key already lies there (the older way),
    /// otherwise C:\POLARIS\konzola.</summary>
    private static string KeyFolder()
    {
        var here = Path.GetFullPath(AppContext.BaseDirectory);
        return Directory.GetFiles(here, "*.kluc").Length > 0 ? here : Home;
    }

    /// <summary>Copies the program to C:\POLARIS\konzola (over an older one), takes centrum.txt along, and adds the
    /// shortcuts and the entry in Settings → Apps. The key and centrum.txt there are never touched.</summary>
    public static string Install(TextWriter o)
    {
        Directory.CreateDirectory(Home);
        var program = Path.Combine(Home, ProgramName);
        var here = Path.GetFullPath(AppContext.BaseDirectory);
        if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), program, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Copy(Environment.ProcessPath!, program, overwrite: true);
            }
            catch (IOException)
            {
                o.WriteLine("Konzola práve beží – zatvor jej okno a spusti inštalačku znova (stará verzia ostáva).");
            }

            var centre = Path.Combine(here, Polaris.Net.CentreFile.Name);
            var target = Path.Combine(Home, Polaris.Net.CentreFile.Name);
            if (File.Exists(centre) && Polaris.Net.CentreFile.Parse(File.ReadAllText(centre)) is not null && !File.Exists(target))
            {
                File.Copy(centre, target);
            }
        }

        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
        {
            try
            {
                Polaris.Windows.Shortcuts.Create(Path.Combine(Environment.GetFolderPath(folder), Title + ".lnk"), program, Home);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Polaris.Windows.SystemCallException)
            {
                o.WriteLine($"Odkaz sa nepodarilo vytvoriť: {e.Message}");
            }
        }

        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true))
        {
            k.SetValue("DisplayName", Title);
            k.SetValue("DisplayVersion", Polaris.Core.Product.Version);
            k.SetValue("Publisher", "POLARIS");
            k.SetValue("DisplayIcon", program);
            k.SetValue("InstallLocation", Home);
            k.SetValue("UninstallString", $"\"{program}\" odinstalovat-konzolu");
            k.SetValue("URLInfoAbout", "https://github.com/tarzanislav/polaris");
            k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }

        return program;
    }

    /// <summary>Settings → Apps → Uninstall: the shortcuts, the entry and the program go; the key of the console and
    /// centrum.txt stay in C:\POLARIS\konzola (a key cannot be made again – the PCs trust exactly this one).</summary>
    public static int Uninstall(TextWriter o)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
        {
            File.Delete(Path.Combine(Environment.GetFolderPath(folder), Title + ".lnk"));
        }

        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        try
        {
            Directory.Delete(Path.Combine(Own, "okno"), recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        // the program cannot delete itself while it runs: a moment later, by the system's own command line
        var program = Path.Combine(Home, ProgramName);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & del /f /q \"{program}\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        o.WriteLine($"POLARIS konzola je odinštalovaná. Kľúč konzoly a centrum.txt ostali v {Home} (zmaž ich, len ak ich už nepotrebuješ).");
        return 0;
    }

    private static string? ReadRunning()
    {
        try
        {
            var parts = File.ReadAllText(Running).Split(' ', 2);
            using var process = Process.GetProcessById(int.Parse(parts[0], CultureInfo.InvariantCulture));
            return process.ProcessName.StartsWith("POLARIS-Konzola", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.StartsWith("polaris-centrum", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
        }
        catch (Exception e) when (e is IOException or FormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int FreePort(int wanted)
    {
        foreach (var port in new[] { wanted, 0 })
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                var got = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                return got;
            }
            catch (SocketException)
            {
            }
        }

        return wanted;
    }

    /// <summary>Microsoft Edge (part of Windows 11), or null.</summary>
    private static string? Edge()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
        if (k?.GetValue(null) is string path && File.Exists(path))
        {
            return path;
        }

        var usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        return File.Exists(usual) ? usual : null;
    }

    /// <summary>The console as an app window of Edge with its own profile (nothing shared with the user's browser).</summary>
    private static void OpenWindow(string address, bool wait)
    {
        var edge = Edge()!;
        var profile = Path.Combine(Own, "okno");
        var start = new ProcessStartInfo(edge) { UseShellExecute = false };
        foreach (var arg in new[] { $"--app={address}", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--window-size=1320,880" })
        {
            start.ArgumentList.Add(arg);
        }

        using var window = Process.Start(start);
        if (wait && window is not null)
        {
            window.WaitForExit();
        }
    }
}
