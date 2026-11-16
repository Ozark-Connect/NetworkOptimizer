using System.Net;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Well-known anycast service addresses: public DNS resolvers announced from many sites at once.
/// GeoLite2 gives each one place (often where the operator registered it), but the instance that
/// answers a trace is whichever site is nearest, so that place says nothing about the hop.
/// </summary>
public static class AnycastAddresses
{
    // IPv4 /24s, as the first three octets. The resolvers that sit in them answer from every site
    // their operator runs.
    private static readonly HashSet<string> Ipv4Slash24 = new(StringComparer.Ordinal)
    {
        "1.1.1", "1.0.0",               // Cloudflare
        "8.8.8", "8.8.4",               // Google
        "9.9.9", "149.112.112",         // Quad9
        "208.67.222", "208.67.220",     // OpenDNS
        "4.2.2",                        // Level 3 / Lumen
        "94.140.14", "94.140.15",       // AdGuard
        "185.228.168", "185.228.169",   // CleanBrowsing
        "76.76.2", "76.76.10",          // Control D
        "64.6.64", "64.6.65",           // Neustar / UltraDNS
        "45.90.28", "45.90.30",         // NextDNS
    };

    private static readonly string[] Ipv6Prefixes =
    {
        "2606:4700:4700:", "2001:4860:4860:", "2620:fe::", "2620:119:35:", "2620:119:53:", "2a07:a8c0:", "2a07:a8c1:",
    };

    /// <summary>Whether the address is one of the known anycast service addresses.</summary>
    public static bool Contains(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address.Trim(), out var ip)) return false;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var octets = ip.ToString().Split('.');
            return Ipv4Slash24.Contains($"{octets[0]}.{octets[1]}.{octets[2]}");
        }
        var text = ip.ToString().ToLowerInvariant();
        return Ipv6Prefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// The metros anycast operators run their sites in: the major interconnection points. An anycast
    /// hop is placed at one of these, picked by its round trip from the last router placed before it.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string CountryCode)> Metros =
    [
        ("Ashburn", "US"), ("New York", "US"), ("Chicago", "US"), ("Dallas", "US"), ("Atlanta", "US"),
        ("Miami", "US"), ("Denver", "US"), ("Los Angeles", "US"), ("San Jose", "US"), ("Seattle", "US"),
        ("Toronto", "CA"), ("Sao Paulo", "BR"), ("London", "GB"), ("Amsterdam", "NL"), ("Frankfurt", "DE"),
        ("Paris", "FR"), ("Madrid", "ES"), ("Stockholm", "SE"), ("Marseille", "FR"), ("Johannesburg", "ZA"),
        ("Mumbai", "IN"), ("Singapore", "SG"), ("Hong Kong", "HK"), ("Tokyo", "JP"), ("Seoul", "KR"), ("Sydney", "AU"),
    ];
}
