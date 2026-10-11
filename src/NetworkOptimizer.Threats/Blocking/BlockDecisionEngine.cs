using System.Text.Json;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>
/// Decides which sources to block. Pure: the caller supplies the evidence and the history.
/// </summary>
public static class BlockDecisionEngine
{
    /// <summary>Earlier blocks within this window count as strikes and lengthen the next ban.</summary>
    public static readonly TimeSpan StrikeWindow = TimeSpan.FromDays(30);

    /// <summary>Each strike multiplies the ban length by this, up to <see cref="BlockingSettings.MaxBanMinutes"/>.</summary>
    public const int StrikeMultiplier = 4;

    // DDoS is left out on purpose: its sources are many and often spoofed.
    private static readonly PatternType[] BlockablePatterns =
        [PatternType.ScanSweep, PatternType.BruteForce, PatternType.ExploitCampaign];

    /// <summary>
    /// New blocks for this cycle. Sources already blocked, or exempt, are skipped.
    /// </summary>
    /// <param name="wafEventCounts">WAF events per source within the window.</param>
    /// <param name="patterns">Patterns seen within the window.</param>
    /// <param name="activeSources">Sources with an active block (Dry Run or Enforce).</param>
    /// <param name="priorStrikes">Blocks per source within <see cref="StrikeWindow"/>.</param>
    public static List<ThreatBlock> Decide(
        BlockingSettings settings,
        DateTime now,
        IReadOnlyDictionary<string, int> wafEventCounts,
        IEnumerable<ThreatPattern> patterns,
        IReadOnlySet<string> activeSources,
        IReadOnlyDictionary<string, int> priorStrikes,
        BlockAllowlist allowlist)
    {
        var decided = new Dictionary<string, ThreatBlock>(StringComparer.OrdinalIgnoreCase);

        void Consider(string ip, BlockTrigger trigger, string reason)
        {
            if (string.IsNullOrWhiteSpace(ip) || decided.ContainsKey(ip) || activeSources.Contains(ip)) return;
            if (allowlist.Exempts(ip) != null) return;
            var strikes = priorStrikes.TryGetValue(ip, out var s) ? s : 0;
            decided[ip] = new ThreatBlock
            {
                SourceIp = ip,
                Trigger = trigger,
                Reason = reason,
                Strikes = strikes,
                Enforced = settings.Mode == BlockingMode.Enforce,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(BanMinutes(settings.BanMinutes, strikes)),
            };
        }

        foreach (var (ip, count) in wafEventCounts.OrderByDescending(kv => kv.Value))
        {
            if (count >= settings.WafEventThreshold)
                Consider(ip, BlockTrigger.WafRepeat, $"{count} WAF events in {settings.WindowMinutes} minutes");
        }

        if (settings.UsePatterns)
        {
            foreach (var p in patterns.Where(p => BlockablePatterns.Contains(p.PatternType)))
            {
                foreach (var ip in SourceIps(p))
                    Consider(ip, BlockTrigger.Pattern, PatternReason(p.PatternType));
            }
        }

        return decided.Values.ToList();
    }

    /// <summary>The ban length in minutes for a source with this many earlier strikes.</summary>
    public static int BanMinutes(int baseMinutes, int strikes)
    {
        double minutes = baseMinutes;
        for (var i = 0; i < strikes && minutes < BlockingSettings.MaxBanMinutes; i++)
            minutes *= StrikeMultiplier;
        return (int)Math.Min(minutes, BlockingSettings.MaxBanMinutes);
    }

    /// <summary>
    /// The entries to send to a target: active Enforce blocks, newest first, capped. Newest wins
    /// because an older entry is closer to expiring anyway.
    /// </summary>
    public static List<ThreatBlock> DesiredSet(IEnumerable<ThreatBlock> blocks, DateTime now, int maxEntries) =>
        blocks.Where(b => b.Enforced && b.IsActive(now))
            .GroupBy(b => b.SourceIp, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(b => b.ExpiresAt).First())
            .OrderByDescending(b => b.CreatedAt)
            .Take(maxEntries)
            .ToList();

    private static string PatternReason(PatternType type) => type switch
    {
        PatternType.ScanSweep => "Scan sweep",
        PatternType.BruteForce => "Brute force",
        PatternType.ExploitCampaign => "Exploit campaign",
        _ => type.ToString()
    };

    private static IEnumerable<string> SourceIps(ThreatPattern p)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(p.SourceIpsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
