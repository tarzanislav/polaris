using System.Runtime.InteropServices;
using System.Text;
using Polaris.Centrum;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// Ctrl+C or the system's request to stop (systemd) ends the server cleanly; other commands just end
var serving = args.Contains("server") && !args.Contains("certifikat");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = serving;
    stop.Cancel();
};
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = serving;
    stop.Cancel();
});

// on Windows the console is a program: a double click installs or updates it and opens its window
if (OperatingSystem.IsWindows())
{
    if (args is [] && !Console.IsInputRedirected)
    {
        return ConsoleApp.Launch(Console.Out, stop.Token);
    }

    if (args is ["okno"])
    {
        return ConsoleApp.Serve(Console.Out, stop.Token);
    }

    if (args is ["aktualizovat-konzolu", var old] && int.TryParse(old, out var pid))
    {
        return ConsoleApp.TakeOver(pid, Console.Out);
    }

    if (args is ["odinstalovat-konzolu"])
    {
        return ConsoleApp.Uninstall(Console.Out);
    }
}

return Commands.Run(args, Console.Out, DateTime.Now, DateTimeOffset.UtcNow, stop: stop.Token);
