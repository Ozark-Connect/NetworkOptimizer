namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Comprehensive audit results for a UniFi network configuration
/// </summary>
public class AuditResult
{
    /// <summary>
    /// Timestamp when the audit was performed
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Client/site name
    /// </summary>
    public string? ClientName { get; init; }

    /// <summary>
    /// Network topology (networks/VLANs discovered)
    /// </summary>
    public List<NetworkInfo> Networks { get; init; } = new();

    /// <summary>
    /// Switches/gateways discovered
    /// </summary>
    public List<SwitchInfo> Switches { get; init; } = new();

    /// <summary>
    /// Wireless clients with detection results
    /// </summary>
    public List<WirelessClientInfo> WirelessClients { get; init; } = new();

    /// <summary>
    /// Offline clients with detection results (from history API)
    /// </summary>
    public List<OfflineClientInfo> OfflineClients { get; init; } = new();

    /// <summary>
    /// All audit issues found
    /// </summary>
    public List<AuditIssue> Issues { get; init; } = new();

    /// <summary>
    /// Critical issues requiring immediate attention
    /// </summary>
    public List<AuditIssue> CriticalIssues => Issues.Where(i => i.Severity == AuditSeverity.Critical).ToList();

    /// <summary>
    /// Recommended improvements
    /// </summary>
    public List<AuditIssue> RecommendedIssues => Issues.Where(i => i.Severity == AuditSeverity.Recommended).ToList();

    /// <summary>
    /// Informational findings - worth knowing but no immediate action required
    /// </summary>
    public List<AuditIssue> InformationalIssues => Issues.Where(i => i.Severity == AuditSeverity.Informational).ToList();

    /// <summary>
    /// Security posture score (0-100)
    /// </summary>
    public int SecurityScore { get; set; }

    /// <summary>
    /// Hardening measures already in place
    /// </summary>
    public List<string> HardeningMeasures { get; init; } = new();

    /// <summary>
    /// Summary statistics
    /// </summary>
    public AuditStatistics Statistics { get; init; } = new();

    /// <summary>
    /// Overall security posture assessment
    /// </summary>
    public SecurityPosture Posture { get; set; }

    /// <summary>
    /// DNS security configuration summary
    /// </summary>
    public DnsSecurityInfo? DnsSecurity { get; set; }
}

/// <summary>
/// DNS security configuration information
/// </summary>
public class DnsSecurityInfo
{
    /// <summary>
    /// Whether DoH is enabled
    /// </summary>
    public bool DohEnabled { get; set; }

    /// <summary>
    /// DoH state (disabled, auto, custom)
    /// </summary>
    public string DohState { get; set; } = "disabled";

    /// <summary>
    /// Configured DoH providers
    /// </summary>
    public List<string> DohProviders { get; set; } = new();

    /// <summary>
    /// Original DoH config names from UniFi (e.g., "NextDNS-fcdba9")
    /// </summary>
    public List<string> DohConfigNames { get; set; } = new();

    /// <summary>
    /// Whether DNS leak prevention (port 53 blocking) is in place
    /// </summary>
    public bool DnsLeakProtection { get; set; }

    /// <summary>
    /// Whether DoT (TCP port 853) is blocked
    /// </summary>
    public bool DotBlocked { get; set; }

    /// <summary>
    /// Whether DoQ (UDP port 853) is blocked
    /// </summary>
    public bool DoqBlocked { get; set; }

    /// <summary>
    /// Whether DoH bypass (public DoH providers on TCP 443) is blocked
    /// </summary>
    public bool DohBypassBlocked { get; set; }

    /// <summary>
    /// Whether DoH3 bypass (public DoH providers on UDP 443/HTTP3) is blocked
    /// </summary>
    public bool Doh3Blocked { get; set; }

    /// <summary>
    /// Configured WAN DNS servers
    /// </summary>
    public List<string> WanDnsServers { get; set; } = new();

    /// <summary>
    /// PTR lookup results for WAN DNS servers (e.g., dns1.nextdns.io)
    /// </summary>
    public List<string?> WanDnsPtrResults { get; set; } = new();

    /// <summary>
    /// Whether WAN DNS servers match the DoH provider
    /// </summary>
    public bool WanDnsMatchesDoH { get; set; }

    /// <summary>
    /// Whether WAN DNS servers are in the correct order (dns1 before dns2 for NextDNS)
    /// </summary>
    public bool WanDnsOrderCorrect { get; set; } = true;

    /// <summary>
    /// Provider name identified from WAN DNS servers
    /// </summary>
    public string? WanDnsProvider { get; set; }

    /// <summary>
    /// Expected DNS provider based on DoH configuration
    /// </summary>
    public string? ExpectedDnsProvider { get; set; }

    /// <summary>
    /// Expected DNS server IPs based on DoH provider
    /// </summary>
    public List<string> ExpectedDnsIps { get; set; } = new();

    /// <summary>
    /// Whether infrastructure devices point DNS to gateway
    /// </summary>
    public bool DeviceDnsPointsToGateway { get; set; } = true;

    /// <summary>
    /// Total number of infrastructure devices checked
    /// </summary>
    public int TotalDevicesChecked { get; set; }

    /// <summary>
    /// Number of devices with correct DNS configuration
    /// </summary>
    public int DevicesWithCorrectDns { get; set; }

    /// <summary>
    /// Number of devices using DHCP-assigned DNS
    /// </summary>
    public int DhcpDeviceCount { get; set; }

    /// <summary>
    /// WAN interfaces without static DNS configured (using ISP DNS)
    /// </summary>
    public List<string> InterfacesWithoutDns { get; set; } = new();

    /// <summary>
    /// WAN interfaces with DNS that doesn't match DoH provider
    /// </summary>
    public List<string> InterfacesWithMismatch { get; set; } = new();

    /// <summary>
    /// DNS servers from mismatched interfaces only
    /// </summary>
    public List<string> MismatchedDnsServers { get; set; } = new();

    /// <summary>
    /// DNS servers from matched interfaces only
    /// </summary>
    public List<string> MatchedDnsServers { get; set; } = new();

    /// <summary>
    /// Whether third-party LAN DNS (like Pi-hole) is detected
    /// </summary>
    public bool HasThirdPartyDns { get; set; }

    /// <summary>
    /// Whether Pi-hole specifically was detected
    /// </summary>
    public bool IsPiholeDetected { get; set; }

    /// <summary>
    /// Name of the third-party DNS provider (e.g., "Pi-hole", "Third-Party LAN DNS")
    /// </summary>
    public string? ThirdPartyDnsProviderName { get; set; }

    /// <summary>
    /// Networks using third-party DNS with their DNS server details
    /// </summary>
    public List<ThirdPartyDnsNetwork> ThirdPartyNetworks { get; set; } = new();

    /// <summary>
    /// Whether DNAT DNS rules exist (redirecting UDP port 53)
    /// </summary>
    public bool HasDnatDnsRules { get; set; }

    /// <summary>
    /// Whether DNAT rules provide full coverage across all DHCP-enabled networks
    /// </summary>
    public bool DnatProvidesFullCoverage { get; set; }

    /// <summary>
    /// The IP address DNS traffic is redirected to
    /// </summary>
    public string? DnatRedirectTarget { get; set; }

    /// <summary>
    /// Network names that have DNAT DNS coverage
    /// </summary>
    public List<string> DnatCoveredNetworks { get; set; } = new();

    /// <summary>
    /// Network names that lack DNAT DNS coverage
    /// </summary>
    public List<string> DnatUncoveredNetworks { get; set; } = new();

    /// <summary>
    /// Whether full DNS protection is in place
    /// </summary>
    public bool FullyProtected => DohEnabled && DnsLeakProtection && DotBlocked && DohBypassBlocked && WanDnsMatchesDoH && DeviceDnsPointsToGateway;
}

/// <summary>
/// Network-specific third-party DNS information
/// </summary>
public class ThirdPartyDnsNetwork
{
    /// <summary>
    /// Name of the network using third-party DNS
    /// </summary>
    public required string NetworkName { get; init; }

    /// <summary>
    /// VLAN ID of the network
    /// </summary>
    public int VlanId { get; init; }

    /// <summary>
    /// IP address of the third-party DNS server
    /// </summary>
    public required string DnsServerIp { get; init; }

    /// <summary>
    /// Provider name (e.g., "Pi-hole", "Third-Party LAN DNS")
    /// </summary>
    public string? DnsProviderName { get; init; }
}

/// <summary>
/// Summary statistics from the audit
/// </summary>
public class AuditStatistics
{
    /// <summary>
    /// Total number of ports across all switches
    /// </summary>
    public int TotalPorts { get; set; }

    /// <summary>
    /// Number of disabled ports
    /// </summary>
    public int DisabledPorts { get; set; }

    /// <summary>
    /// Number of active/up ports
    /// </summary>
    public int ActivePorts { get; set; }

    /// <summary>
    /// Number of ports with MAC restrictions
    /// </summary>
    public int MacRestrictedPorts { get; set; }

    /// <summary>
    /// Number of ports with port security enabled
    /// </summary>
    public int PortSecurityEnabledPorts { get; set; }

    /// <summary>
    /// Number of isolated ports
    /// </summary>
    public int IsolatedPorts { get; set; }

    /// <summary>
    /// Number of unprotected active ports
    /// </summary>
    public int UnprotectedActivePorts { get; set; }

    /// <summary>
    /// Percentage of ports that are hardened (0-100)
    /// </summary>
    public double HardeningPercentage => TotalPorts > 0
        ? (double)(MacRestrictedPorts + DisabledPorts) / TotalPorts * 100
        : 0;
}

/// <summary>
/// Overall security posture assessment
/// </summary>
public enum SecurityPosture
{
    /// <summary>
    /// Excellent security posture - no critical issues
    /// </summary>
    Excellent,

    /// <summary>
    /// Good security posture - minimal issues
    /// </summary>
    Good,

    /// <summary>
    /// Fair security posture - some improvements needed
    /// </summary>
    Fair,

    /// <summary>
    /// Poor security posture - needs attention
    /// </summary>
    NeedsAttention,

    /// <summary>
    /// Critical security posture - immediate action required
    /// </summary>
    Critical
}
