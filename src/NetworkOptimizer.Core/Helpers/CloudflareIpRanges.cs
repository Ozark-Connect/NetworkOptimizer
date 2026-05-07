using System.Net;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Well-known Cloudflare IP ranges for detecting Cloudflare-proxied port forward restrictions.
/// These ranges rarely change. Source: https://www.cloudflare.com/ips/
/// </summary>
public static class CloudflareIpRanges
{
    /// <summary>
    /// Cloudflare IPv4 CIDR ranges.
    /// </summary>
    public static readonly string[] IPv4Ranges =
    [
        "173.245.48.0/20",
        "103.21.244.0/22",
        "103.22.200.0/22",
        "103.31.4.0/22",
        "141.101.64.0/18",
        "108.162.192.0/18",
        "190.93.240.0/20",
        "188.114.96.0/20",
        "197.234.240.0/22",
        "198.41.128.0/17",
        "162.158.0.0/15",
        "104.16.0.0/13",
        "104.24.0.0/14",
        "172.64.0.0/13",
        "131.0.72.0/22"
    ];

    /// <summary>
    /// Cloudflare IPv6 CIDR ranges.
    /// </summary>
    public static readonly string[] IPv6Ranges =
    [
        "2400:cb00::/32",
        "2606:4700::/32",
        "2803:f800::/32",
        "2405:b500::/32",
        "2405:8100::/32",
        "2a06:98c0::/29",
        "2c0f:f248::/32"
    ];

    /// <summary>
    /// All Cloudflare CIDR ranges (IPv4 + IPv6).
    /// </summary>
    public static readonly string[] AllRanges = [.. IPv4Ranges, .. IPv6Ranges];

    private const int MaxNonCloudflareHosts = 10;

    /// <summary>
    /// Check if a list of IP addresses/CIDRs represents a Cloudflare-restricted configuration.
    /// Returns true if the list is primarily Cloudflare ranges, tolerating a small number of
    /// individual host IPs (e.g., management/home IPs) that people commonly add alongside CF ranges.
    /// Non-Cloudflare entries must be single hosts (no CIDR or /32 or /128) - large non-CF subnets
    /// indicate a different restriction strategy, not a Cloudflare list with extras.
    /// </summary>
    /// <param name="addresses">List of IPs or CIDRs from a firewall group or source restriction</param>
    /// <returns>True if the list is a Cloudflare IP restriction (possibly with a few management IPs)</returns>
    public static bool IsCloudflareOnly(IEnumerable<string>? addresses)
    {
        if (addresses == null)
            return false;

        var list = addresses.ToList();
        if (list.Count == 0)
            return false;

        int cloudflareCount = 0;
        int nonCloudflareHostCount = 0;

        foreach (var address in list)
        {
            if (IsCloudflareAddress(address))
            {
                cloudflareCount++;
                continue;
            }

            var trimmed = address.Trim();
            bool isSingleHost = !trimmed.Contains('/')
                || trimmed.EndsWith("/32")
                || trimmed.EndsWith("/128");

            if (!isSingleHost)
                return false;

            nonCloudflareHostCount++;
            if (nonCloudflareHostCount > MaxNonCloudflareHosts)
                return false;
        }

        return cloudflareCount > 0;
    }

    /// <summary>
    /// Check if a list of IP addresses/CIDRs contains any Cloudflare ranges.
    /// </summary>
    /// <param name="addresses">List of IPs or CIDRs from a firewall group or source restriction</param>
    /// <returns>True if at least one address matches a known Cloudflare range</returns>
    public static bool ContainsCloudflareRange(IEnumerable<string>? addresses)
    {
        if (addresses == null)
            return false;

        return addresses.Any(IsCloudflareAddress);
    }

    /// <summary>
    /// Check if a single IP address or CIDR is a known Cloudflare range.
    /// Matches exact CIDR entries or single IPs that fall within a Cloudflare range.
    /// </summary>
    private static bool IsCloudflareAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var trimmed = address.Trim();

        // Exact match against known ranges (most common case for firewall groups)
        if (AllRanges.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            return true;

        // If it's a CIDR, check if it's contained within a Cloudflare range
        if (trimmed.Contains('/'))
        {
            // A configured CIDR is "Cloudflare" if a known CF range covers it entirely
            foreach (var cfRange in AllRanges)
            {
                if (NetworkUtilities.CidrCoversSubnet(cfRange, trimmed))
                    return true;
            }

            return false;
        }

        // Single IP - check if it falls within any Cloudflare range
        if (IPAddress.TryParse(trimmed, out _))
        {
            return NetworkUtilities.IsIpInAnySubnet(trimmed, AllRanges);
        }

        return false;
    }
}
