using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Enriches client IP addresses from history data.
/// This is needed because clients connected to UX/UX7 devices may not have IPs
/// in the stat/sta response (UniFi API bug).
/// </summary>
public static class ClientIpEnricher
{
    /// <summary>
    /// Builds a MAC-to-IP lookup from client data (active clients or history).
    /// </summary>
    /// <param name="clients">Client entries from the UniFi API (active or history)</param>
    /// <returns>Dictionary mapping MAC addresses to their best available IP</returns>
    public static Dictionary<string, string> BuildMacToIpLookup(IEnumerable<UniFiClientDetailResponse> clients)
    {
        if (clients == null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return clients
            .Where(c => !string.IsNullOrEmpty(c.Mac) && !string.IsNullOrEmpty(c.BestIp))
            .GroupBy(c => c.Mac, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().BestIp!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the IP address for a client, falling back to history if the primary IP is missing.
    /// </summary>
    /// <param name="primaryIp">The IP from the stat/sta response</param>
    /// <param name="mac">The client's MAC address</param>
    /// <param name="macToIpLookup">Lookup table built from history</param>
    /// <returns>The primary IP if available, otherwise the IP from history, otherwise null</returns>
    public static string? GetEnrichedIp(string? primaryIp, string? mac, Dictionary<string, string> macToIpLookup)
    {
        // Use primary IP if available
        if (!string.IsNullOrEmpty(primaryIp))
            return primaryIp;

        // Try to get IP from history using MAC
        if (!string.IsNullOrEmpty(mac) && macToIpLookup.TryGetValue(mac, out var historyIp))
            return historyIp;

        return null;
    }
}
