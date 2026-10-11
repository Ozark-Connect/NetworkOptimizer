using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>
/// Addresses Automatic Blocking never blocks, whatever the evidence. Built-ins first: anything not
/// publicly routable (LAN, VPN, Tailscale's CGNAT range) and Cloudflare's edge, where a block would
/// cut off every visitor of a proxied site. Then the user's list and the source addresses of
/// enabled Trusted User and Infrastructure filters.
/// </summary>
public sealed class BlockAllowlist
{
    /// <summary>
    /// Cloudflare's published ranges (https://www.cloudflare.com/ips/), checked against
    /// api.cloudflare.com/client/v4/ips on 2026-10-11. Cloudflare adds ranges there before using them.
    /// </summary>
    public static readonly IReadOnlyList<string> CloudflareRanges =
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18",
        "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17",
        "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32",
        "2a06:98c0::/29", "2c0f:f248::/32",
    ];

    private readonly List<string> _ranges = [];
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="userList">The site's own allowlist setting.</param>
    /// <param name="filters">The site's noise filters; Trusted User and Infrastructure ones exempt their source.</param>
    /// <param name="wanAddresses">WAN addresses of this site and every site it shares blocks with.</param>
    public BlockAllowlist(string? userList, IEnumerable<ThreatNoiseFilter>? filters = null, IEnumerable<string>? wanAddresses = null)
    {
        _ranges.AddRange(CloudflareRanges);
        foreach (var entry in ParseList(userList).Concat(wanAddresses ?? []))
            Add(entry);
        foreach (var f in filters ?? [])
        {
            if (f.Enabled && f.Category != ThreatFilterCategory.Noise && !string.IsNullOrWhiteSpace(f.SourceIp))
                Add(f.SourceIp.Trim());
        }
    }

    private void Add(string entry)
    {
        if (entry.Contains('/')) _ranges.Add(entry);
        else _exact.Add(entry);
    }

    /// <summary>The reason an address must not be blocked, or null when it may be.</summary>
    public string? Exempts(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var addr)) return "not an IP address";
        if (!NetworkUtilities.IsPublicIpAddress(addr)) return "not a public address";
        if (_exact.Contains(ip) || _exact.Contains(addr.ToString())) return "on the allowlist";
        if (NetworkUtilities.IsIpInAnySubnet(ip, _ranges)) return "on the allowlist";
        return null;
    }

    /// <summary>
    /// One spelling per address: IPv6 compressed and lowercased, IPv4-mapped IPv6 unwrapped to IPv4.
    /// Null when it is not an IP address. Blocks are stored and matched in this form.
    /// </summary>
    public static string? Normalize(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !System.Net.IPAddress.TryParse(ip.Trim(), out var addr)) return null;
        return (addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr).ToString();
    }

    /// <summary>Splits a user allowlist on commas, whitespace, and newlines.</summary>
    public static IEnumerable<string> ParseList(string? list) =>
        (list ?? "").Split([',', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The entries of a user allowlist that are not an IP address or CIDR range.</summary>
    public static List<string> Invalid(string? list) =>
        ParseList(list).Where(e => !IsIpOrCidr(e)).ToList();

    private static bool IsIpOrCidr(string entry)
    {
        var slash = entry.IndexOf('/');
        if (slash < 0) return System.Net.IPAddress.TryParse(entry, out _);
        if (!System.Net.IPAddress.TryParse(entry[..slash], out var addr) || !int.TryParse(entry[(slash + 1)..], out var bits))
            return false;
        var max = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return bits >= 0 && bits <= max;
    }
}
