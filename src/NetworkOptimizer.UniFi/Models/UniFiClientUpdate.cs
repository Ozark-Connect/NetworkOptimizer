using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// The body of a <c>PUT rest/user/{id}</c>: a client's settings as UniFi Network's client form
/// sends them. Null fields are left out of the request, and the Console keeps their stored values,
/// so a body carrying only <see cref="Name"/> renames the client and changes nothing else.
/// </summary>
[VendorSpecific("UniFi", "rest/user PUT merges top-level keys; shape from UniFi Network's client settings form")]
public sealed class UniFiClientUpdate
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The client's alias. Empty clears it, and UniFi Network shows the hostname again.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Sent by UniFi Network's form alongside <see cref="Name"/> with the same value; not stored on the record.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("local_dns_record_enabled")]
    public bool? LocalDnsRecordEnabled { get; set; }

    [JsonPropertyName("local_dns_record")]
    public string? LocalDnsRecord { get; set; }

    [JsonPropertyName("virtual_network_override_enabled")]
    public bool? VirtualNetworkOverrideEnabled { get; set; }

    [JsonPropertyName("virtual_network_override_id")]
    public string? VirtualNetworkOverrideId { get; set; }

    [JsonPropertyName("network_members_group_ids")]
    public List<string>? NetworkMembersGroupIds { get; set; }

    [JsonPropertyName("usergroup_id")]
    public string? UsergroupId { get; set; }

    /// <summary>Whether the client has a DHCP reservation (UniFi Network's Fixed IP Address).</summary>
    [JsonPropertyName("use_fixedip")]
    public bool? UseFixedIp { get; set; }

    [JsonPropertyName("fixed_ip")]
    public string? FixedIp { get; set; }

    [JsonPropertyName("fixed_ap_enabled")]
    public bool? FixedApEnabled { get; set; }

    /// <summary>
    /// The full form as UniFi Network sends it, filled from the client's stored record. Change the
    /// fields to write and send the result; every other setting goes back as it was read.
    /// </summary>
    public static UniFiClientUpdate FromRecord(UniFiClientResponse record) => new()
    {
        Name = record.Name,
        DisplayName = record.Name,
        LocalDnsRecordEnabled = record.LocalDnsRecordEnabled,
        LocalDnsRecord = record.LocalDnsRecord ?? "",
        VirtualNetworkOverrideEnabled = record.VirtualNetworkOverrideEnabled,
        VirtualNetworkOverrideId = record.VirtualNetworkOverrideId ?? "",
        NetworkMembersGroupIds = record.NetworkMembersGroupIds?.ToList() ?? new List<string>(),
        UsergroupId = record.UsergroupId ?? "",
        UseFixedIp = record.UseFixedIp,
        FixedIp = record.FixedIp,
        FixedApEnabled = record.FixedApEnabled ?? false
    };

    /// <summary>The JSON body, without the fields left null.</summary>
    public string ToRequestJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
