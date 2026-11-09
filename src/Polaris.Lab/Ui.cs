using Polaris.Core;

namespace Polaris.Lab;

/// <summary>The console of the owner: Slovak text, colours, choices by one key (no typing of commands).</summary>
internal static class Ui
{
    public static void Title(string text)
    {
        Console.WriteLine();
        Line(text, ConsoleColor.Cyan);
        Line(new string('─', Math.Min(72, Math.Max(text.Length, 20))), ConsoleColor.DarkCyan);
    }

    public static void Line(string text, ConsoleColor? color = null)
    {
        if (color is { } c)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = c;
            Console.WriteLine(text);
            Console.ForegroundColor = old;
        }
        else
        {
            Console.WriteLine(text);
        }
    }

    public static void Ok(string text) => Line(text, ConsoleColor.Green);

    public static void Warn(string text) => Line(text, ConsoleColor.Yellow);

    public static void Error(string text) => Line(text, ConsoleColor.Red);

    public static void Check(CheckResult r)
    {
        var color = r.Level switch
        {
            CheckLevel.Ok => ConsoleColor.Green,
            CheckLevel.Info => ConsoleColor.Gray,
            CheckLevel.Warning => ConsoleColor.Yellow,
            _ => ConsoleColor.Red,
        };
        Line($"  [{r.Mark}] {r.Name}: {r.Detail}", color);
    }

    public static void Option(char key, string text) => Line($"  {key}  {text}");

    /// <summary>Waits for one of the keys; returns it.</summary>
    public static char Choose(string keys)
    {
        Console.Write("Vyber: ");
        while (true)
        {
            var key = Console.ReadKey(intercept: true).KeyChar;
            if (keys.Contains(key, StringComparison.Ordinal))
            {
                Console.WriteLine(key);
                return key;
            }
        }
    }

    public static bool Confirm(string question)
    {
        Console.WriteLine(question + "  1 = áno, 2 = nie");
        return Choose("12") == '1';
    }

    public static int Number(string question, int min, int max, int fallback)
    {
        while (true)
        {
            Console.Write($"{question} ({min}–{max}, Enter = {fallback}): ");
            var text = Console.ReadLine()?.Trim() ?? "";
            if (text.Length == 0)
            {
                return fallback;
            }

            if (int.TryParse(text, out var n) && n >= min && n <= max)
            {
                return n;
            }

            Warn("Zadaj iba číslice v rozsahu.");
        }
    }

    public static void Pause()
    {
        Console.Write("Stlač ľubovoľný kláves…");
        Console.ReadKey(intercept: true);
        Console.WriteLine();
    }

    private static DateTime _lastProgress;

    public static void Progress(string what, long done, long total, DateTime started)
    {
        if (DateTime.UtcNow - _lastProgress < TimeSpan.FromMilliseconds(250) && done < total)
        {
            return;
        }

        _lastProgress = DateTime.UtcNow;
        var pct = total > 0 ? done * 100 / total : 0;
        var seconds = (DateTime.UtcNow - started).TotalSeconds;
        var speed = seconds > 0 ? done / seconds : 0;
        var left = speed > 0 ? TimeSpan.FromSeconds((total - done) / speed) : TimeSpan.Zero;
        Console.Write($"\r  {what}: {pct,3} %  ({Format.Gb(done)} z {Format.Gb(total)}, {Format.Decimal(speed / 1e6, 0)} MB/s, zostáva ~{Format.Duration(left)})      ");
        if (done >= total)
        {
            Console.WriteLine();
        }
    }

    public static void Countdown(string text, int seconds)
    {
        for (var s = seconds; s > 0; s--)
        {
            Console.Write($"\r{text} o {s} s… (Esc = zrušiť)   ");
            var until = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < until)
            {
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
                {
                    Console.WriteLine();
                    throw new OperationCanceledException();
                }

                Thread.Sleep(50);
            }
        }

        Console.WriteLine();
    }
}
