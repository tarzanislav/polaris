namespace Polaris.Core;

public enum CheckLevel
{
    Ok,
    Info,
    Warning,

    /// <summary>The conversion must not start.</summary>
    Blocker,
}

/// <summary>One finding of the check of a PC (shown to the owner, saved to the report).</summary>
public sealed record CheckResult(string Name, CheckLevel Level, string Detail)
{
    public string Mark => Level switch
    {
        CheckLevel.Ok => "OK",
        CheckLevel.Info => "i ",
        CheckLevel.Warning => "! ",
        _ => "X ",
    };
}

public static class CheckRules
{
    /// <summary>Windows editions that can start from a VHDX: Pro, Education, Enterprise (and their variants), not Home.</summary>
    public static bool EditionSupportsNativeBoot(string? editionId) =>
        editionId is not null
        && !editionId.StartsWith("Core", StringComparison.OrdinalIgnoreCase)
        && (editionId.Contains("Professional", StringComparison.OrdinalIgnoreCase)
            || editionId.Contains("Education", StringComparison.OrdinalIgnoreCase)
            || editionId.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
            || editionId.Contains("IoT", StringComparison.OrdinalIgnoreCase));

    /// <summary>Space the conversion needs on the host volume: the base (the used data) and room for the sessions and
    /// the page file of the layered Windows.</summary>
    public static long RequiredFreeBytes(long usedBytes, long memoryBytes) =>
        (long)(usedBytes * 1.10) + memoryBytes + 12L * 1000 * 1000 * 1000;

    public static bool CanStart(IEnumerable<CheckResult> results) => results.All(r => r.Level != CheckLevel.Blocker);
}
