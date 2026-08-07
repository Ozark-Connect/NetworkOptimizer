using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// A policy-based route from v2 trafficroutes: which devices' traffic, to which destinations,
/// leaves by which network. Only the fields monitoring reads are modelled.
/// </summary>
[VendorSpecific("UniFi", "v2/api/site/{site}/trafficroutes")]
public class UniFiTrafficRouteResponse
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("enabled")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool Enabled { get; set; }

    /// <summary>
    /// What the route matches on: "INTERNET" is every destination, and the only value that makes
    /// the route a statement about where a device's traffic leaves in general. "IP", "DOMAIN" and
    /// "REGION" steer a slice of it, which says nothing about where a probe to somewhere else goes.
    /// </summary>
    [JsonPropertyName("matching_target")]
    public string? MatchingTarget { get; set; }

    /// <summary>The network the matched traffic leaves by; a WAN's id for the routes we care about.</summary>
    [JsonPropertyName("network_id")]
    public string? NetworkId { get; set; }

    /// <summary>
    /// Whether matched traffic is dropped rather than re-routed when the network is down. False
    /// means a failover silently sends it out another WAN - see MonitoringTarget.WanInterface.
    /// </summary>
    [JsonPropertyName("kill_switch_enabled")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool KillSwitchEnabled { get; set; }

    [JsonPropertyName("target_devices")]
    public List<UniFiTrafficRouteTargetDevice> TargetDevices { get; set; } = new();
}

/// <summary>One device a traffic route applies to, or the marker that it applies to all of them.</summary>
[VendorSpecific("UniFi", "trafficroutes target_devices[]")]
public class UniFiTrafficRouteTargetDevice
{
    [JsonPropertyName("client_mac")]
    public string? ClientMac { get; set; }

    /// <summary>"CLIENT" names a MAC in <see cref="ClientMac"/>; "ALL_CLIENTS" names every device.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
