using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// WAN provider capabilities (ISP upload/download speeds).
/// </summary>
public class WanProviderCapabilities
{
    [JsonPropertyName("upload_kilobits_per_second")]
    public int UploadKilobitsPerSecond { get; set; }

    [JsonPropertyName("download_kilobits_per_second")]
    public int DownloadKilobitsPerSecond { get; set; }

    /// <summary>Upload speed in Mbps</summary>
    public int UploadMbps => UploadKilobitsPerSecond / 1000;

    /// <summary>Download speed in Mbps</summary>
    public int DownloadMbps => DownloadKilobitsPerSecond / 1000;
}

/// <summary>
/// JSON converter that handles int values that may come as strings, empty strings, or null.
/// UniFi API sometimes returns VLAN IDs as strings or empty strings instead of numbers.
/// </summary>
public class FlexibleIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt32(),
            JsonTokenType.String when int.TryParse(reader.GetString(), out var value) => value,
            JsonTokenType.String => null, // Empty string or non-numeric string
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }
}

/// <summary>
/// Response from GET /api/s/{site}/rest/networkconf
/// Represents a network/VLAN configuration
/// </summary>
public class UniFiNetworkConfig
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("site_id")]
    public string SiteId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = string.Empty; // "corporate", "guest", "wan", "vlan-only", "remote-user-vpn"

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("is_nat")]
    public bool IsNat { get; set; }

    [JsonPropertyName("vlan_enabled")]
    public bool VlanEnabled { get; set; }

    [JsonPropertyName("vlan")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Vlan { get; set; }

    // IP configuration
    [JsonPropertyName("dhcpd_enabled")]
    public bool DhcpdEnabled { get; set; }

    [JsonPropertyName("dhcpd_start")]
    public string? DhcpdStart { get; set; }

    [JsonPropertyName("dhcpd_stop")]
    public string? DhcpdStop { get; set; }

    [JsonPropertyName("dhcpd_leasetime")]
    public int DhcpdLeasetime { get; set; }

    [JsonPropertyName("dhcpd_dns_enabled")]
    public bool DhcpdDnsEnabled { get; set; }

    [JsonPropertyName("dhcpd_dns_1")]
    public string? DhcpdDns1 { get; set; }

    [JsonPropertyName("dhcpd_dns_2")]
    public string? DhcpdDns2 { get; set; }

    [JsonPropertyName("dhcpd_dns_3")]
    public string? DhcpdDns3 { get; set; }

    [JsonPropertyName("dhcpd_dns_4")]
    public string? DhcpdDns4 { get; set; }

    [JsonPropertyName("dhcpd_gateway_enabled")]
    public bool DhcpdGatewayEnabled { get; set; }

    [JsonPropertyName("dhcpd_gateway")]
    public string? DhcpdGateway { get; set; }

    [JsonPropertyName("dhcpd_time_offset_enabled")]
    public bool DhcpdTimeOffsetEnabled { get; set; }

    [JsonPropertyName("dhcpd_time_offset")]
    public int DhcpdTimeOffset { get; set; }

    [JsonPropertyName("ip_subnet")]
    public string? IpSubnet { get; set; }

    [JsonPropertyName("ipv6_interface_type")]
    public string? Ipv6InterfaceType { get; set; }

    [JsonPropertyName("ipv6_pd_interface")]
    public string? Ipv6PdInterface { get; set; }

    [JsonPropertyName("ipv6_pd_prefixid")]
    public string? Ipv6PdPrefixid { get; set; }

    [JsonPropertyName("ipv6_pd_start")]
    public string? Ipv6PdStart { get; set; }

    [JsonPropertyName("ipv6_pd_stop")]
    public string? Ipv6PdStop { get; set; }

    [JsonPropertyName("ipv6_ra_enabled")]
    public bool Ipv6RaEnabled { get; set; }

    [JsonPropertyName("ipv6_ra_priority")]
    public string? Ipv6RaPriority { get; set; }

    [JsonPropertyName("ipv6_ra_valid_lifetime")]
    public int Ipv6RaValidLifetime { get; set; }

    [JsonPropertyName("ipv6_ra_preferred_lifetime")]
    public int Ipv6RaPreferredLifetime { get; set; }

    // WAN configuration
    [JsonPropertyName("wan_networkgroup")]
    public string? WanNetworkgroup { get; set; }

    [JsonPropertyName("wan_type")]
    public string? WanType { get; set; } // "dhcp", "static", "pppoe"

    /// <summary>
    /// The interface name for this WAN (e.g., "eth4", "eth0")
    /// Used for mapping to TC monitor interfaces
    /// </summary>
    [JsonPropertyName("wan_ifname")]
    public string? WanIfname { get; set; }

    /// <summary>
    /// WAN type version 2 interface name
    /// </summary>
    [JsonPropertyName("wan_type_v2")]
    public string? WanTypeV2 { get; set; }

    /// <summary>
    /// WAN load balance type ("failover-only" or "weighted")
    /// </summary>
    [JsonPropertyName("wan_load_balance_type")]
    public string? WanLoadBalanceType { get; set; }

    /// <summary>
    /// WAN load balance weight (for weighted load balancing)
    /// </summary>
    [JsonPropertyName("wan_load_balance_weight")]
    public int? WanLoadBalanceWeight { get; set; }

    [JsonPropertyName("wan_ip")]
    public string? WanIp { get; set; }

    [JsonPropertyName("wan_netmask")]
    public string? WanNetmask { get; set; }

    [JsonPropertyName("wan_gateway")]
    public string? WanGateway { get; set; }

    [JsonPropertyName("wan_dns1")]
    public string? WanDns1 { get; set; }

    [JsonPropertyName("wan_dns2")]
    public string? WanDns2 { get; set; }

    [JsonPropertyName("wan_username")]
    public string? WanUsername { get; set; }

    [JsonPropertyName("wan_password")]
    public string? WanPassword { get; set; }

    [JsonPropertyName("wan_egress_qos")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? WanEgressQos { get; set; }

    [JsonPropertyName("wan_smartq_enabled")]
    public bool WanSmartqEnabled { get; set; }

    [JsonPropertyName("wan_smartq_up_rate")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? WanSmartqUpRate { get; set; }

    [JsonPropertyName("wan_smartq_down_rate")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? WanSmartqDownRate { get; set; }

    /// <summary>
    /// WAN provider capabilities (upload/download speeds).
    /// Only present on WAN networks with ISP speed configured.
    /// </summary>
    [JsonPropertyName("wan_provider_capabilities")]
    public WanProviderCapabilities? WanProviderCapabilities { get; set; }

    // VPN configuration
    [JsonPropertyName("vpn_type")]
    public string? VpnType { get; set; } // "pptp", "l2tp", "openvpn", "wireguard"

    [JsonPropertyName("radiusprofile_id")]
    public string? RadiusprofileId { get; set; }

    [JsonPropertyName("l2tp_interface")]
    public string? L2tpInterface { get; set; }

    [JsonPropertyName("l2tp_local_wan_ip")]
    public string? L2tpLocalWanIp { get; set; }

    [JsonPropertyName("x_l2tp_psk")]
    public string? XL2tpPsk { get; set; }

    [JsonPropertyName("openvpn_mode")]
    public string? OpenvpnMode { get; set; }

    [JsonPropertyName("openvpn_remote_host")]
    public string? OpenvpnRemoteHost { get; set; }

    [JsonPropertyName("openvpn_remote_port")]
    public int? OpenvpnRemotePort { get; set; }

    // Domain configuration
    [JsonPropertyName("domain_name")]
    public string? DomainName { get; set; }

    [JsonPropertyName("dhcpd_ip_1")]
    public string? DhcpdIp1 { get; set; }

    [JsonPropertyName("dhcpd_ip_2")]
    public string? DhcpdIp2 { get; set; }

    [JsonPropertyName("dhcpd_ip_3")]
    public string? DhcpdIp3 { get; set; }

    // Multicast DNS
    [JsonPropertyName("mdns_enabled")]
    public bool MdnsEnabled { get; set; }

    [JsonPropertyName("upnp_lan_enabled")]
    public bool UpnpLanEnabled { get; set; }

    // IGMP
    [JsonPropertyName("igmp_snooping")]
    public bool IgmpSnooping { get; set; }

    // Network group
    [JsonPropertyName("networkgroup")]
    public string? Networkgroup { get; set; }

    // Internet access
    [JsonPropertyName("internet_access_enabled")]
    public bool InternetAccessEnabled { get; set; }

    // Auto scaling
    [JsonPropertyName("dhcpd_unifi_controller")]
    public string? DhcpdUnifiController { get; set; }

    // Scheduling
    [JsonPropertyName("schedule")]
    public List<string>? Schedule { get; set; }

    [JsonPropertyName("schedule_enabled")]
    public bool ScheduleEnabled { get; set; }

    // Content filtering
    [JsonPropertyName("contentfilter_enabled")]
    public bool ContentfilterEnabled { get; set; }

    /// <summary>
    /// The firewall zone ID for this network.
    /// Used to identify which firewall zone this network belongs to (e.g., LAN zone vs WAN/External zone).
    /// </summary>
    [JsonPropertyName("firewall_zone_id")]
    public string? FirewallZoneId { get; set; }
}
