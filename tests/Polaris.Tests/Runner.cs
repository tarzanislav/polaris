using System.Diagnostics;
using System.Reflection;

namespace Polaris.Tests;

/// <summary>Marks a test: a public static parameterless method.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class CheckAttribute : Attribute;

internal sealed class CheckFailed(string message) : Exception(message);

internal static class Expect
{
    public static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new CheckFailed(what);
        }
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new CheckFailed($"{what}: čakal som <{expected}>, je <{actual}>");
        }
    }

    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
        {
            throw new CheckFailed($"{what}: čakal som [{string.Join(", ", e)}], je [{string.Join(", ", a)}]");
        }
    }

    public static void Throws<TException>(Action action, string what)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new CheckFailed($"{what}: čakal som výnimku {typeof(TException).Name}");
    }
}

internal static class Runner
{
    /// <summary>Runs all checks, or only those of the classes named in the arguments. Exit code 1 when any fails.</summary>
    public static int Main(string[] args)
    {
        var methods = typeof(Runner).Assembly.GetTypes()
            .Where(t => args.Length == 0 || args.Contains(t.Name))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<CheckAttribute>() is not null)
            .OrderBy(m => m.DeclaringType!.Name, StringComparer.Ordinal)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
        var failed = 0;
        var clock = Stopwatch.StartNew();
        foreach (var method in methods)
        {
            var name = $"{method.DeclaringType!.Name}.{method.Name}";
            try
            {
                method.Invoke(null, null);
                Console.WriteLine($"  ok    {name}");
            }
            catch (TargetInvocationException e)
            {
                failed++;
                Console.WriteLine($"  CHYBA {name}: {e.InnerException?.Message}");
                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                {
                    Console.WriteLine($"::error title={name}::{e.InnerException?.Message}");
                }
            }
        }

        Console.WriteLine($"{methods.Count - failed} z {methods.Count} v poriadku ({clock.Elapsed.TotalSeconds:F1} s)");
        return failed == 0 && methods.Count > 0 ? 0 : 1;
    }
}
