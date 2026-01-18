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
    public const string IsolationBypassed = "ISOLATION_BYPASSED";
    public const string FwAnyAny = "FW_ANY_ANY";
    public const string MgmtMissingUnifiAccess = "MGMT_MISSING_UNIFI_ACCESS";
    public const string MgmtMissingAfcAccess = "MGMT_MISSING_AFC_ACCESS";
    public const string MgmtMissingNtpAccess = "MGMT_MISSING_NTP_ACCESS";
    public const string MgmtMissing5gAccess = "MGMT_MISSING_5G_ACCESS";
    public const string ExternalZoneNotDetected = "EXTERNAL_ZONE_NOT_DETECTED";

    // VLAN Security
    public const string IotVlan = "IOT-VLAN-001";
    public const string WifiIotVlan = "WIFI-IOT-VLAN-001";
    public const string CameraVlan = "CAMERA-VLAN-001";
    public const string WifiCameraVlan = "WIFI-CAMERA-VLAN-001";
    public const string InfraNotOnMgmt = "INFRA_NOT_ON_MGMT";
    public const string DnsLeakage = "DNS_LEAKAGE";
    public const string DnsSharedServers = "DNS_SHARED_SERVERS";
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
    public const string VlanSubnetMismatch = "WIFI-VLAN-SUBNET-001";
    public const string WiredSubnetMismatch = "PORT-SUBNET-001";

    // UPnP Security
    public const string UpnpEnabled = "UPNP_ENABLED";
    public const string UpnpNonHomeNetwork = "UPNP_NON_HOME_NETWORK";
    public const string UpnpPrivilegedPort = "UPNP_PRIVILEGED_PORT";
    public const string UpnpPortsExposed = "UPNP_PORTS_EXPOSED";
    public const string StaticPortForward = "STATIC_PORT_FORWARD";
    public const string StaticPrivilegedPort = "STATIC_PRIVILEGED_PORT";

    // DNS Security
    public const string DnsNoDoh = "DNS_NO_DOH";
    public const string DnsDohAuto = "DNS_DOH_AUTO";
    public const string DnsNo53Block = "DNS_NO_53_BLOCK";
    public const string Dns53PartialCoverage = "DNS_53_PARTIAL_COVERAGE";
    public const string DnsNoDotBlock = "DNS_NO_DOT_BLOCK";
    public const string DnsNoDohBlock = "DNS_NO_DOH_BLOCK";
    public const string DnsNoDoqBlock = "DNS_NO_DOQ_BLOCK";
    public const string DnsIsp = "DNS_ISP";
    public const string DnsWanMismatch = "DNS_WAN_MISMATCH";
    public const string DnsWanOrder = "DNS_WAN_ORDER";
    public const string DnsWanNoStatic = "DNS_WAN_NO_STATIC";
    public const string DnsDeviceMisconfigured = "DNS_DEVICE_MISCONFIGURED";
    public const string DnsThirdPartyDetected = "DNS_THIRD_PARTY_DETECTED";
    public const string DnsUnknownConfig = "DNS_UNKNOWN_CONFIG";
    public const string DnsInconsistentConfig = "DNS_INCONSISTENT_CONFIG";

    // DNS DNAT Issues
    public const string DnsDnatPartialCoverage = "DNS_DNAT_PARTIAL_COVERAGE";
    public const string DnsDnatSingleIp = "DNS_DNAT_SINGLE_IP";
    public const string DnsDnatWrongDestination = "DNS_DNAT_WRONG_DESTINATION";
    public const string DnsDnatRestrictedDestination = "DNS_DNAT_RESTRICTED_DESTINATION";
}
