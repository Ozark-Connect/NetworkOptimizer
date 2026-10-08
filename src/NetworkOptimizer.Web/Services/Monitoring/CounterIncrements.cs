namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>Sums the growth of a cumulative counter across a series of readings.</summary>
public static class CounterIncrements
{
    /// <summary>
    /// Total of every rise between consecutive non-null readings. A drop is a counter reset (a
    /// reboot or a re-sync), not negative errors, so it adds nothing and the next rise counts from
    /// the new value. Null when no two readings could be compared.
    /// </summary>
    public static long? Total(IEnumerable<long?> counters)
    {
        long total = 0;
        var any = false;
        long? prev = null;
        foreach (var v in counters)
        {
            if (v is not long cur) continue;
            if (prev is long p)
            {
                var delta = cur - p;
                if (delta > 0) total += delta;
                any = true;
            }
            prev = cur;
        }
        return any ? total : null;
    }
}
