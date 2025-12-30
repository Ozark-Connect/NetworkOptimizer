using System.Net;

namespace NetworkOptimizer.Audit.Dns;

/// <summary>
/// Registry of known DNS-over-HTTPS providers
/// </summary>
public static class DohProviderRegistry
{
    /// <summary>
    /// Known DoH providers with their configuration details
    /// </summary>
    public static readonly IReadOnlyDictionary<string, DohProviderInfo> Providers = new Dictionary<string, DohProviderInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["NextDNS"] = new DohProviderInfo
        {
            Name = "NextDNS",
            StampPrefix = "nextdns",
            Hostnames = new[] { "nextdns.io" }, // PTR returns dns1.nextdns.io, dns2.nextdns.io
            DnsIps = Array.Empty<string>(), // NextDNS uses profile-specific IPs - must use PTR lookup
            SupportsFiltering = true,
            HasCustomConfig = true,
            Description = "NextDNS - Privacy-focused DNS with filtering"
        },
        ["AdGuard"] = new DohProviderInfo
        {
            Name = "AdGuard",
            StampPrefix = "adguard",
            Hostnames = new[] { "dns.adguard.com", "dns-family.adguard.com", "dns-unfiltered.adguard.com" },
            DnsIps = new[] { "94.140.14.14", "94.140.15.15", "94.140.14.15", "94.140.15.16" },
            SupportsFiltering = true,
            HasCustomConfig = true,
            Description = "AdGuard DNS with ad blocking"
        },
        ["Cloudflare"] = new DohProviderInfo
        {
            Name = "Cloudflare",
            StampPrefix = "cloudflare",
            Hostnames = new[] { "cloudflare-dns.com", "1dot1dot1dot1.cloudflare-dns.com", "one.one.one.one", "dns.cloudflare.com", "mozilla.cloudflare-dns.com", "family.cloudflare-dns.com", "security.cloudflare-dns.com" },
            DnsIps = new[] { "1.1.1.1", "1.0.0.1", "1.1.1.2", "1.0.0.2", "1.1.1.3", "1.0.0.3" },
            SupportsFiltering = false,
            HasCustomConfig = false,
            Description = "Cloudflare 1.1.1.1 DNS"
        },
        ["Google"] = new DohProviderInfo
        {
            Name = "Google",
            StampPrefix = "google",
            Hostnames = new[] { "dns.google", "dns.google.com", "8888.google", "dns64.dns.google" },
            DnsIps = new[] { "8.8.8.8", "8.8.4.4" },
            SupportsFiltering = false,
            HasCustomConfig = false,
            Description = "Google Public DNS"
        },
        ["Quad9"] = new DohProviderInfo
        {
            Name = "Quad9",
            StampPrefix = "quad9",
            Hostnames = new[] { "dns.quad9.net", "dns9.quad9.net", "dns10.quad9.net", "dns11.quad9.net" },
            DnsIps = new[] { "9.9.9.9", "149.112.112.112", "9.9.9.10", "149.112.112.10" },
            SupportsFiltering = true,
            HasCustomConfig = false,
            Description = "Quad9 Security-focused DNS"
        },
        ["OpenDNS"] = new DohProviderInfo
        {
            Name = "OpenDNS",
            StampPrefix = "opendns",
            Hostnames = new[] { "doh.opendns.com", "doh.familyshield.opendns.com", "doh.sandbox.opendns.com" },
            DnsIps = new[] { "208.67.222.222", "208.67.220.220", "208.67.222.123", "208.67.220.123" },
            SupportsFiltering = true,
            HasCustomConfig = false,
            Description = "Cisco OpenDNS"
        },
        ["CleanBrowsing"] = new DohProviderInfo
        {
            Name = "CleanBrowsing",
            StampPrefix = "cleanbrowsing",
            Hostnames = new[] { "doh.cleanbrowsing.org" },
            DnsIps = new[] { "185.228.168.168", "185.228.169.168", "185.228.168.10", "185.228.169.11" },
            SupportsFiltering = true,
            HasCustomConfig = false,
            Description = "CleanBrowsing Family-safe DNS"
        },
        ["LibreDNS"] = new DohProviderInfo
        {
            Name = "LibreDNS",
            StampPrefix = "libredns",
            Hostnames = new[] { "doh.libredns.gr" },
            DnsIps = new[] { "116.202.176.26" },
            SupportsFiltering = false,
            HasCustomConfig = false,
            Description = "LibreDNS - Privacy-focused"
        }
    };

    /// <summary>
    /// Identify a provider from a hostname
    /// </summary>
    public static DohProviderInfo? IdentifyProvider(string hostname)
    {
        if (string.IsNullOrEmpty(hostname))
            return null;

        var hostLower = hostname.ToLowerInvariant();

        foreach (var provider in Providers.Values)
        {
            if (provider.Hostnames.Any(h => hostLower.Contains(h.ToLowerInvariant())))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// Identify a provider from a server name (e.g., "NextDNS-fcdba9")
    /// </summary>
    public static DohProviderInfo? IdentifyProviderFromName(string serverName)
    {
        if (string.IsNullOrEmpty(serverName))
            return null;

        var nameLower = serverName.ToLowerInvariant();

        foreach (var kvp in Providers)
        {
            if (nameLower.StartsWith(kvp.Key.ToLowerInvariant()))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Identify a provider from a DNS IP address (static lookup only)
    /// </summary>
    public static DohProviderInfo? IdentifyProviderFromIp(string ip)
    {
        if (string.IsNullOrEmpty(ip))
            return null;

        foreach (var provider in Providers.Values)
        {
            if (provider.MatchesIp(ip))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// Identify a provider from a DNS IP address using PTR lookup for verification
    /// </summary>
    public static async Task<(DohProviderInfo? Provider, string? ReverseDns)> IdentifyProviderFromIpWithPtrAsync(string ip)
    {
        if (string.IsNullOrEmpty(ip))
            return (null, null);

        // First try static lookup
        var staticProvider = IdentifyProviderFromIp(ip);

        // For NextDNS (and other providers with dynamic IPs), verify with PTR lookup
        string? reverseDns = null;
        try
        {
            reverseDns = await ReverseDnsLookupAsync(ip);
        }
        catch
        {
            // PTR lookup failed - fall back to static match
        }

        if (!string.IsNullOrEmpty(reverseDns))
        {
            // Try to identify provider from the reverse DNS hostname
            var ptrProvider = IdentifyProvider(reverseDns);
            if (ptrProvider != null)
            {
                return (ptrProvider, reverseDns);
            }
        }

        return (staticProvider, reverseDns);
    }

    /// <summary>
    /// Perform a reverse DNS (PTR) lookup on an IP address
    /// </summary>
    public static async Task<string?> ReverseDnsLookupAsync(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var ipAddress))
            return null;

        try
        {
            var hostEntry = await System.Net.Dns.GetHostEntryAsync(ipAddress);
            return hostEntry.HostName;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Information about a DoH provider
/// </summary>
public class DohProviderInfo
{
    public required string Name { get; init; }
    public required string StampPrefix { get; init; }
    public required string[] Hostnames { get; init; }
    public required string[] DnsIps { get; init; }
    public required bool SupportsFiltering { get; init; }
    public required bool HasCustomConfig { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// Check if a given IP matches this provider's expected DNS IPs
    /// </summary>
    public bool MatchesIp(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        return DnsIps.Any(expected =>
            expected.EndsWith('.')
                ? ip.StartsWith(expected) // Prefix match (e.g., "45.90.28.")
                : ip == expected);        // Exact match
    }
}
