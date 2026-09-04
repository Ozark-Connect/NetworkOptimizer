using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// What width a radio's band is really asked for: the widest any client that can roam to the
/// radio has negotiated over the lookback, from the agent's client history plus the clients on
/// it now. Site-wide because devices roam, with the AP lock honored: a device locked to another
/// AP will never arrive here, and a device locked to this AP always counts. Locks come from the
/// console's full client roster, so an offline device is honored too.
/// </summary>
public static class ApAgentWidthDemand
{
    /// <summary>How far back a client's negotiated width still counts as demand.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(7);

    /// <summary>
    /// Sets <see cref="RadioSnapshot.MeasuredMaxNegotiatedWidth"/> on every radio the evidence
    /// reaches. Returns how many radios got a value.
    /// </summary>
    /// <param name="aps">The AP snapshots.</param>
    /// <param name="clients">The console's active clients, for live negotiated widths and their locks.</param>
    /// <param name="history">Per client MAC, per band tag, the widest negotiated width over the lookback.</param>
    /// <param name="apLocks">AP locks from the console's full client roster (client MAC to AP MAC),
    /// so a device that is offline right now is still honored; the active list adds its own.</param>
    public static int Apply(
        IEnumerable<AccessPointSnapshot> aps,
        IReadOnlyList<WirelessClientSnapshot> clients,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> history,
        IReadOnlyDictionary<string, string>? apLocks = null)
    {
        var lockedTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (apLocks != null)
            foreach (var (mac, apMac) in apLocks)
                lockedTo[Normalize(mac)] = Normalize(apMac);
        foreach (var c in clients)
            if (c.FixedApEnabled && !string.IsNullOrEmpty(c.FixedApMac))
                lockedTo[Normalize(c.Mac)] = Normalize(c.FixedApMac);

        var set = 0;
        foreach (var ap in aps)
        {
            var apMac = Normalize(ap.Mac);
            foreach (var radio in ap.Radios.Where(r => r.Channel.HasValue && r.Band != RadioBand.Unknown))
            {
                var tag = BandTag(radio.Band);
                int max = 0;

                foreach (var (clientMac, byBand) in history)
                {
                    if (!byBand.TryGetValue(tag, out var width) || width <= 0) continue;
                    if (lockedTo.TryGetValue(clientMac, out var lockAp) && lockAp != apMac) continue;
                    max = Math.Max(max, width);
                }

                foreach (var c in clients)
                {
                    if (!c.IsOnline || c.Band != radio.Band || c.NegotiatedWidth is not > 0) continue;
                    if (lockedTo.TryGetValue(Normalize(c.Mac), out var lockAp) && lockAp != apMac) continue;
                    max = Math.Max(max, c.NegotiatedWidth.Value);
                }

                if (max <= 0) continue;
                radio.MeasuredMaxNegotiatedWidth = max;
                set++;
            }
        }
        return set;
    }

    private static string BandTag(RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => "2.4ghz",
        RadioBand.Band5GHz => "5ghz",
        RadioBand.Band6GHz => "6ghz",
        _ => ""
    };

    private static string Normalize(string? mac) => (mac ?? "").Trim().ToLowerInvariant();
}
