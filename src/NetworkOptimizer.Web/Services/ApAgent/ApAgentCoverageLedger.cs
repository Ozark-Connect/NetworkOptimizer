using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Which access points the AP Agent path is currently the source for.
///
/// The console's stat/sta covers every AP on the site, so an AP served by its own agent must be
/// written from one source, not both. A claim is made only by a poll that actually returned fresh
/// telemetry and expires on its own, so an agent that goes quiet hands its access point straight
/// back to the console path instead of leaving a gap.
/// </summary>
public sealed class ApAgentCoverageLedger
{
    /// <summary>
    /// How long a claim stands without being renewed. Long enough to ride out a couple of missed
    /// polls, short enough that a dead agent does not hold its access point dark.
    /// </summary>
    public static readonly TimeSpan ClaimTtl = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<string, DateTime> _claims = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records that this access point's agent just answered with fresh telemetry.</summary>
    public void Claim(string apMac, DateTime at) => _claims[Normalize(apMac)] = at;

    /// <summary>Hands one access point back to the console path immediately.</summary>
    public void Release(string apMac) => _claims.TryRemove(Normalize(apMac), out _);

    /// <summary>Hands every access point back.</summary>
    public void ReleaseAll() => _claims.Clear();

    /// <summary>Whether the AP Agent path owns this access point's clients right now.</summary>
    public bool Covers(string apMac, DateTime now)
        => _claims.TryGetValue(Normalize(apMac), out var at) && now - at <= ClaimTtl;

    /// <summary>How many access points are claimed, expiry included.</summary>
    public int ActiveClaims(DateTime now) => _claims.Count(kv => now - kv.Value <= ClaimTtl);

    /// <summary>
    /// Drops claims for access points that are no longer on the site, so a removed AP cannot keep
    /// an entry alive forever.
    /// </summary>
    public void RetainOnly(IReadOnlySet<string> apMacs)
    {
        foreach (var key in _claims.Keys)
        {
            if (!apMacs.Contains(key)) _claims.TryRemove(key, out _);
        }
    }

    private static string Normalize(string? mac) => (mac ?? "").Trim().ToLowerInvariant();
}
