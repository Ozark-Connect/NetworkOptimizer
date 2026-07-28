namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Bounds how often path analysis is re-attempted for a single speed-test result.
///
/// The retry itself is essential and stays: topology and the UniFi client list routinely lag a test by
/// seconds, so the analysis taken at test time legitimately fails and a later one succeeds. What is not
/// safe is that the retry is armed by a READ - any result whose path is not valid re-qualifies on every
/// load of the page.
///
/// For a path that can never resolve, that condition never clears. The server is not in the client list
/// (a container on a bridge network, a host on a subnet UniFi does not manage), or the test ran at a
/// remote site and is being traced from the local WAN. On WAN Speed Test the analysis completing raises
/// an event, the page reloads its history, the reload re-arms the retry, and that is a closed cycle with
/// nothing to stop it - observed at ~7,000 attempts a second, 450% CPU, taking the browser tab with it.
///
/// So the retry keeps running, on a cooldown and a bounded number of times per result. A path that comes
/// good on the second or third look still comes good; one that never will stops asking.
/// </summary>
public static class PathAnalysisRetryGate
{
    /// <summary>Minimum gap between attempts on the same result. The first attempt is never delayed.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    /// <summary>Attempts allowed per result. Beyond this the path is treated as unresolvable.</summary>
    private const int MaxAttempts = 5;

    /// <summary>Entries are dropped well past the 30-minute window the callers retry within.</summary>
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(1);

    private static readonly Dictionary<string, (DateTime LastAttempt, int Attempts)> Entries = new();
    private static readonly object Sync = new();

    /// <summary>
    /// Claims the right to analyse <paramref name="resultId"/> now, or returns false when that result is
    /// inside its cooldown or has used its attempts. <paramref name="scope"/> separates the services, whose
    /// result IDs come from different tables and would otherwise collide.
    /// </summary>
    public static bool TryClaim(string scope, int resultId)
    {
        var key = $"{scope}:{resultId}";
        var now = DateTime.UtcNow;

        lock (Sync)
        {
            if (Entries.TryGetValue(key, out var entry))
            {
                if (entry.Attempts >= MaxAttempts || now - entry.LastAttempt < Cooldown)
                    return false;

                Entries[key] = (now, entry.Attempts + 1);
                return true;
            }

            Prune(now);
            Entries[key] = (now, 1);
            return true;
        }
    }

    /// <summary>Forgets a result, so a genuine re-analysis (a WAN reassignment) starts from a clean slate.</summary>
    public static void Forget(string scope, int resultId)
    {
        lock (Sync)
            Entries.Remove($"{scope}:{resultId}");
    }

    private static void Prune(DateTime now)
    {
        if (Entries.Count < 256) return;

        var stale = Entries.Where(e => now - e.Value.LastAttempt > EntryLifetime).Select(e => e.Key).ToList();
        foreach (var key in stale)
            Entries.Remove(key);
    }
}
