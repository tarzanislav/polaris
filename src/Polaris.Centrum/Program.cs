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

return Commands.Run(args, Console.Out, DateTime.Now, DateTimeOffset.UtcNow, stop: stop.Token);
