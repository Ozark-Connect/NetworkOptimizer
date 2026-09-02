using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Copies onto the console-sourced client and AP snapshots what only the AP Agent knows about an
/// association: the join signal, how long it has stood, whether the client answered a roam nudge,
/// what width it negotiated, and the last hour's latency and stalls; and per AP, the exact client
/// count. A client or AP no agent covers is left exactly as the console built it.
/// </summary>
public static class ApAgentClientEnricher
{
    /// <summary>Facts older than this are not copied.</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Applies the evidence in place. Returns how many clients were enriched.
    /// </summary>
    /// <param name="aps">The AP snapshots; each covered one gets its measured client count.</param>
    /// <param name="clients">The client snapshots.</param>
    /// <param name="factsFor">The agent's latest per-association facts for an AP MAC.</param>
    /// <param name="hourStatsFor">The last hour's latency median and stall delta for (AP MAC, client MAC).</param>
    /// <param name="clientCountFor">The agent's current client count for an AP MAC; null when not covered.</param>
    /// <param name="now">The clock; defaults to UTC now.</param>
    public static int Apply(
        IEnumerable<AccessPointSnapshot> aps,
        IEnumerable<WirelessClientSnapshot> clients,
        Func<string, IReadOnlyList<ApAgentClientFacts>> factsFor,
        Func<string, string, (double? MedianLatencyMs, int? Stalls)> hourStatsFor,
        Func<string, int?> clientCountFor,
        DateTime? now = null)
    {
        var clock = now ?? DateTime.UtcNow;
        var index = new Dictionary<(string Ap, string Client), WirelessClientSnapshot>();
        foreach (var c in clients)
        {
            if (!c.IsOnline || string.IsNullOrEmpty(c.ApMac)) continue;
            var ap = Normalize(c.ApMac);
            index.TryAdd((ap, Normalize(c.Mac)), c);
            // An MLO client is keyed on its MLD MAC by the agent; the console may name a link.
            foreach (var link in c.MloLinks)
                if (!string.IsNullOrEmpty(link.Mac)) index.TryAdd((ap, Normalize(link.Mac)), c);
        }

        var enriched = 0;
        foreach (var ap in aps)
        {
            if (string.IsNullOrEmpty(ap.Mac)) continue;
            var apMac = Normalize(ap.Mac);
            ap.MeasuredClientCount = clientCountFor(apMac);

            foreach (var facts in factsFor(apMac))
            {
                if (clock - facts.At > FreshWindow) continue;
                if (!index.TryGetValue((apMac, Normalize(facts.ClientMac)), out var client)) continue;

                client.JoinSignal = facts.JoinSignal;
                client.AssociatedFor = facts.AssociatedFor;
                client.RoamNudges = facts.RoamNudges;
                client.RoamNudgesAccepted = facts.RoamNudgesAccepted;
                client.NegotiatedWidth = facts.NegotiatedWidth;

                var (latency, stalls) = hourStatsFor(apMac, facts.ClientMac);
                client.MeasuredLatencyAvgMs = latency;
                client.MeasuredTcpStalls = stalls;
                enriched++;
            }
        }
        return enriched;
    }

    private static string Normalize(string? mac) => (mac ?? "").Trim().ToLowerInvariant();
}
