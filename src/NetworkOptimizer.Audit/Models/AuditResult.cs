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
    /// Items to investigate
    /// </summary>
    public List<AuditIssue> InvestigateIssues => Issues.Where(i => i.Severity == AuditSeverity.Investigate).ToList();

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
    /// Whether DNS leak prevention (port 53 blocking) is in place
    /// </summary>
    public bool DnsLeakProtection { get; set; }

    /// <summary>
    /// Whether DoT (port 853) is blocked
    /// </summary>
    public bool DotBlocked { get; set; }

    /// <summary>
    /// Whether DoH bypass (public DoH providers) is blocked
    /// </summary>
    public bool DohBypassBlocked { get; set; }

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
    /// Whether full DNS protection is in place
    /// </summary>
    public bool FullyProtected => DohEnabled && DnsLeakProtection && DotBlocked && DohBypassBlocked && WanDnsMatchesDoH && DeviceDnsPointsToGateway;
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
