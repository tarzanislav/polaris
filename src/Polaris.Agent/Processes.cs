using System.Diagnostics;
using System.Text;

namespace Polaris.Agent;

/// <summary>How a started program ended: its exit code and the last lines it wrote (for people, never parsed for
/// decisions – the agent decides by exit codes and by reading the state again).</summary>
public sealed record ProcessResult(int ExitCode, string OutputTail)
{
    public string Code => $"{ExitCode} (0x{unchecked((uint)ExitCode):X8})";
}

/// <summary>Starts programs without a shell, with each argument passed as its own item, and waits with a time limit.</summary>
public static class Processes
{
    public const int TailLength = 2000;

    public static ProcessResult Run(string program, IEnumerable<string> arguments, TimeSpan limit) =>
        Start(program, info =>
        {
            foreach (var a in arguments)
            {
                info.ArgumentList.Add(a);
            }
        }, limit);

    /// <summary>For commands taken from the list of installed programs, where the arguments are one text already.</summary>
    public static ProcessResult RunRaw(string program, string arguments, TimeSpan limit) =>
        Start(program, info => info.Arguments = arguments, limit);

    private static ProcessResult Start(string program, Action<ProcessStartInfo> setArguments, TimeSpan limit)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        setArguments(info);
        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Program {program} sa nespustil.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(limit))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"{Path.GetFileName(program)} nedobehol do {limit.TotalMinutes:0} minút, bol ukončený.");
        }

        process.WaitForExit(); // the rest of the output
        lock (output)
        {
            var text = output.ToString().Trim();
            return new ProcessResult(process.ExitCode, text.Length <= TailLength ? text : "…" + text[^TailLength..]);
        }
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
            if (output.Length > 4 * TailLength)
            {
                output.Remove(0, output.Length - 2 * TailLength);
            }
        }
    }
}
