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

    /// <summary>
    /// Special-use space no public list entry may touch. An entry overlapping any of these is dropped
    /// whole: FireHOL-style lists carry bogons such as 10.0.0.0/8, which would cut off every LAN.
    /// </summary>
    private static readonly IReadOnlyList<System.Net.IPNetwork> SpecialUse =
    [
        .. new[]
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.168.0.0/16", "198.18.0.0/15", "224.0.0.0/4", "240.0.0.0/4",
            "::/8", "64:ff9b::/96", "fc00::/7", "fe80::/10", "ff00::/8",
        }.Select(System.Net.IPNetwork.Parse)
    ];

    private static readonly System.Net.IPNetwork GlobalUnicastV6 = System.Net.IPNetwork.Parse("2000::/3");

    private readonly List<string> _ranges = [];
    private readonly List<System.Net.IPNetwork> _networks = [];
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="userList">The site's own allowlist setting.</param>
    /// <param name="filters">The site's noise filters; source-wide Trusted User and Infrastructure ones exempt their source.</param>
    /// <param name="wanAddresses">WAN addresses of this site and every site it shares blocks with.</param>
    /// <param name="cloudflareRanges">Cloudflare's current ranges; the built-in copy when null.</param>
    public BlockAllowlist(string? userList, IEnumerable<ThreatNoiseFilter>? filters = null,
        IEnumerable<string>? wanAddresses = null, IEnumerable<string>? cloudflareRanges = null)
    {
        foreach (var entry in (cloudflareRanges ?? CloudflareRanges).Concat(ParseList(userList)).Concat(wanAddresses ?? []))
            Add(entry);
        // Only a filter on the whole source exempts it: one scoped to a port or destination vouches
        // for that traffic alone.
        foreach (var f in filters ?? [])
        {
            if (f.Enabled && f.Category != ThreatFilterCategory.Noise && !string.IsNullOrWhiteSpace(f.SourceIp) &&
                f.DestIp == null && f.DestPort == null)
                Add(f.SourceIp.Trim());
        }
    }

    private void Add(string entry)
    {
        if (entry.Contains('/'))
        {
            _ranges.Add(entry);
            if (System.Net.IPNetwork.TryParse(entry, out var n)) _networks.Add(n);
        }
        else
        {
            _exact.Add(entry);
            if (System.Net.IPAddress.TryParse(entry, out var a))
                _networks.Add(new System.Net.IPNetwork(a, a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32));
        }
    }

    /// <summary>
    /// The reason a public list's IP or CIDR must not be applied, or null when it may be. A range
    /// is refused if it overlaps anything protected at all, not only if it is inside it.
    /// </summary>
    public string? ExemptsRange(string entry)
    {
        if (!entry.Contains('/'))
            return Exempts(entry);
        if (!System.Net.IPNetwork.TryParse(entry, out var range)) return "not an IP range";
        if (range.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !Overlaps(range, GlobalUnicastV6))
            return "not a public range";
        if (SpecialUse.Any(s => Overlaps(range, s))) return "overlaps private or reserved space";
        if (_networks.Any(n => Overlaps(range, n))) return "overlaps the allowlist";
        return null;
    }

    private static bool Overlaps(System.Net.IPNetwork a, System.Net.IPNetwork b) =>
        a.BaseAddress.AddressFamily == b.BaseAddress.AddressFamily
        && (a.Contains(b.BaseAddress) || b.Contains(a.BaseAddress));

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
