namespace Polaris.Net;

/// <summary>The two centres keep each other whole (design 4.16). Each tells the other what it has (the high-water mark
/// per origin); the other sends back the entries it is missing. After a round both hold the same entries. A centre that
/// was off catches up when it comes back.</summary>
public static class Sync
{
    /// <summary>What one centre offers another: the entries the other does not have yet, given the other's high-water.</summary>
    public static List<LogEntry> Offer(EventLog from, IReadOnlyDictionary<string, long> theirHighWater) =>
        from.Since(theirHighWater).ToList();

    /// <summary>One round of exchange between two centres in the same process (for the agent's test bench and CI). In the
    /// real system the same two steps go over the network. Returns how many entries each side took over.</summary>
    public static (int ToA, int ToB) Exchange(
        EventLog a,
        EventLog b,
        Func<string, VerifyingKey?> originKey)
    {
        var toA = 0;
        foreach (var entry in Offer(b, a.HighWater()))
        {
            if (a.Accept(entry, originKey))
            {
                toA++;
            }
        }

        var toB = 0;
        foreach (var entry in Offer(a, b.HighWater()))
        {
            if (b.Accept(entry, originKey))
            {
                toB++;
            }
        }

        return (toA, toB);
    }
}
