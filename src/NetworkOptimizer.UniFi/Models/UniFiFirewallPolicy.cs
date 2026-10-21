using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// The fields Network Optimizer reads from GET v2/api/site/{site}/firewall-policies. One policy
/// covers one source zone, one destination zone, and one IP version.
/// </summary>
public class UniFiFirewallPolicy
{
    [JsonPropertyName("_id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    /// <summary>"ALLOW", "BLOCK", or "REJECT".</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    /// <summary>"IPV4", "IPV6", or "BOTH".</summary>
    [JsonPropertyName("ip_version")] public string IpVersion { get; set; } = string.Empty;
    /// <summary>Evaluation order within a zone pair: user policies from 10000, predefined from 30000.</summary>
    [JsonPropertyName("index")] public long Index { get; set; }
    [JsonPropertyName("predefined")] public bool Predefined { get; set; }
    /// <summary>"ALL", or "CUSTOM" with <see cref="ConnectionStates"/> naming the states matched.</summary>
    [JsonPropertyName("connection_state_type")] public string? ConnectionStateType { get; set; }
    /// <summary>"NEW", "ESTABLISHED", "RELATED", "INVALID" when <see cref="ConnectionStateType"/> is "CUSTOM".</summary>
    [JsonPropertyName("connection_states")] public List<string>? ConnectionStates { get; set; }
    [JsonPropertyName("source")] public UniFiFirewallPolicyEndpoint Source { get; set; } = new();
    [JsonPropertyName("destination")] public UniFiFirewallPolicyEndpoint Destination { get; set; } = new();
}

/// <summary>A policy's source or destination.</summary>
public class UniFiFirewallPolicyEndpoint
{
    [JsonPropertyName("zone_id")] public string ZoneId { get; set; } = string.Empty;
    /// <summary>"ANY", "IP", ...</summary>
    [JsonPropertyName("matching_target")] public string? MatchingTarget { get; set; }
    /// <summary>"OBJECT" when <see cref="IpGroupId"/> names a firewall group.</summary>
    [JsonPropertyName("matching_target_type")] public string? MatchingTargetType { get; set; }
    /// <summary>The referenced group's <c>_id</c> (not its external_id); "" when unused.</summary>
    [JsonPropertyName("ip_group_id")] public string? IpGroupId { get; set; }
}

/// <summary>
/// Body for POST v2/api/site/{site}/firewall-policies: a policy that blocks every source in an
/// address group from one zone to another. Shape as UniFi Network's own UI sends it (verified on
/// 10.6 and 11.x).
/// </summary>
public sealed class UniFiGroupBlockPolicyWrite
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    [JsonPropertyName("action")] public string Action { get; init; } = "BLOCK";
    [JsonPropertyName("create_allow_respond")] public bool CreateAllowRespond { get; init; }
    /// <summary>"IPV4" or "IPV6"; must match the group's family.</summary>
    [JsonPropertyName("ip_version")] public required string IpVersion { get; init; }
    [JsonPropertyName("logging")] public bool Logging { get; init; }
    // No report_flow: 11.x's UI sends it, 10.x has no such field, and 11.x accepts the body without it.
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = "all";
    [JsonPropertyName("connection_state_type")] public string ConnectionStateType { get; init; } = "ALL";
    /// <summary>The states matched when <see cref="ConnectionStateType"/> is "CUSTOM"; left out of the body otherwise.</summary>
    [JsonPropertyName("connection_states")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ConnectionStates { get; init; }
    [JsonPropertyName("match_ip_sec")] public bool MatchIpSec { get; init; }
    [JsonPropertyName("schedule")] public object Schedule { get; init; } = new { mode = "ALWAYS" };
    [JsonPropertyName("source")] public required object Source { get; init; }
    [JsonPropertyName("destination")] public required object Destination { get; init; }

    /// <summary>
    /// Builds the outbound counterpart: traffic from <paramref name="sourceZoneId"/> to any address in
    /// the group in <paramref name="destinationZoneId"/> (External). The destination object mirrors the
    /// source object UniFi Network's UI sends for a Network List.
    /// </summary>
    public static UniFiGroupBlockPolicyWrite CreateToGroup(string name, string description, string ipVersion,
        string sourceZoneId, string destinationZoneId, string groupId) => new()
    {
        Name = name,
        Description = description,
        IpVersion = ipVersion,
        Source = new Dictionary<string, object>
        {
            ["zone_id"] = sourceZoneId,
            ["match_mac"] = false,
            ["port_matching_type"] = "ANY",
            ["matching_target"] = "ANY",
        },
        Destination = new Dictionary<string, object>
        {
            ["zone_id"] = destinationZoneId,
            ["port_matching_type"] = "ANY",
            ["matching_target"] = "IP",
            ["matching_target_type"] = "OBJECT",
            ["ip_group_id"] = groupId,
            ["match_opposite_ips"] = false,
        },
    };

    /// <summary>The connection states a source block matches: new connections only, so replies to
    /// connections the LAN opened still pass UniFi's own Allow Return Traffic.</summary>
    public static readonly IReadOnlyList<string> NewConnectionsOnly = ["NEW"];

    /// <summary>
    /// Builds the policy for one zone pair. With <paramref name="newConnectionsOnly"/> it matches only
    /// <see cref="NewConnectionsOnly"/>; otherwise every state.
    /// </summary>
    public static UniFiGroupBlockPolicyWrite Create(string name, string description, string ipVersion,
        string sourceZoneId, string groupId, string destinationZoneId, bool newConnectionsOnly = false) => new()
    {
        Name = name,
        Description = description,
        IpVersion = ipVersion,
        ConnectionStateType = newConnectionsOnly ? "CUSTOM" : "ALL",
        ConnectionStates = newConnectionsOnly ? NewConnectionsOnly : null,
        Source = new Dictionary<string, object>
        {
            ["zone_id"] = sourceZoneId,
            ["match_mac"] = false,
            ["port_matching_type"] = "ANY",
            ["matching_target"] = "IP",
            ["matching_target_type"] = "OBJECT",
            ["ip_group_id"] = groupId,
            ["match_opposite_ips"] = false,
        },
        Destination = new Dictionary<string, object>
        {
            ["zone_id"] = destinationZoneId,
            ["port_matching_type"] = "ANY",
            ["matching_target"] = "ANY",
        },
    };
}
