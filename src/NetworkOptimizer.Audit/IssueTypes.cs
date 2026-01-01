namespace NetworkOptimizer.Audit;

/// <summary>
/// Constants for all audit issue types. Use these instead of magic strings.
/// Using constants ensures typos become compile errors rather than silent failures.
/// </summary>
public static class IssueTypes
{
    // Firewall Rules
    public const string AllowExceptionPattern = "ALLOW_EXCEPTION_PATTERN";
    public const string AllowSubvertsDeny = "ALLOW_SUBVERTS_DENY";
    public const string DenyShadowsAllow = "DENY_SHADOWS_ALLOW";
    public const string PermissiveRule = "PERMISSIVE_RULE";
    public const string BroadRule = "BROAD_RULE";
    public const string OrphanedRule = "ORPHANED_RULE";
    public const string MissingIsolation = "MISSING_ISOLATION";
    public const string FwAnyAny = "FW_ANY_ANY";
    public const string MgmtMissingUnifiAccess = "MGMT_MISSING_UNIFI_ACCESS";
    public const string MgmtMissingAfcAccess = "MGMT_MISSING_AFC_ACCESS";
    public const string MgmtMissingNtpAccess = "MGMT_MISSING_NTP_ACCESS";
    public const string MgmtMissing5gAccess = "MGMT_MISSING_5G_ACCESS";

    // VLAN Security
    public const string IotVlan = "IOT-VLAN-001";
    public const string WifiIotVlan = "WIFI-IOT-VLAN-001";
    public const string CameraVlan = "CAMERA-VLAN-001";
    public const string WifiCameraVlan = "WIFI-CAMERA-VLAN-001";
    public const string DnsLeakage = "DNS_LEAKAGE";
    public const string RoutingEnabled = "ROUTING_ENABLED";
    public const string MgmtDhcpEnabled = "MGMT_DHCP_ENABLED";
    public const string SecurityNetworkNotIsolated = "SECURITY_NETWORK_NOT_ISOLATED";
    public const string MgmtNetworkNotIsolated = "MGMT_NETWORK_NOT_ISOLATED";
    public const string IotNetworkNotIsolated = "IOT_NETWORK_NOT_ISOLATED";
    public const string SecurityNetworkHasInternet = "SECURITY_NETWORK_HAS_INTERNET";
    public const string MgmtNetworkHasInternet = "MGMT_NETWORK_HAS_INTERNET";

    // Port Security
    public const string MacRestriction = "MAC-RESTRICT-001";
    public const string UnusedPort = "UNUSED-PORT-001";
    public const string PortIsolation = "PORT-ISOLATION-001";

    // DNS Security
    public const string DnsNoDoh = "DNS_NO_DOH";
    public const string DnsDohAuto = "DNS_DOH_AUTO";
    public const string DnsNo53Block = "DNS_NO_53_BLOCK";
    public const string DnsNoDotBlock = "DNS_NO_DOT_BLOCK";
    public const string DnsNoDohBlock = "DNS_NO_DOH_BLOCK";
    public const string DnsIsp = "DNS_ISP";
    public const string DnsWanMismatch = "DNS_WAN_MISMATCH";
    public const string DnsWanOrder = "DNS_WAN_ORDER";
    public const string DnsWanNoStatic = "DNS_WAN_NO_STATIC";
    public const string DnsDeviceMisconfigured = "DNS_DEVICE_MISCONFIGURED";
}
