using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// A Port Automation (Network 11.1+, v2 port-automations): applies a port profile to a switch port when a device of a
/// given vendor or MAC OUI connects. Shape from the 11.1.58 web UI's request body.
/// </summary>
[VendorSpecific("UniFi", "v2 port-automations, Network 11.1")]
public class UniFiPortAutomation
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleScalarStringConverter))]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    [JsonConverter(typeof(FlexibleScalarStringConverter))]
    public string? Name { get; set; }

    [JsonPropertyName("enabled")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool Enabled { get; set; } = true;

    /// <summary>The port profile it applies.</summary>
    [JsonPropertyName("port_profile_id")]
    [JsonConverter(typeof(FlexibleScalarStringConverter))]
    public string? PortProfileId { get; set; }

    [JsonPropertyName("match")]
    [JsonConverter(typeof(FlexibleObjectConverter<UniFiPortAutomationMatch>))]
    public UniFiPortAutomationMatch? Match { get; set; }
}

/// <summary>What a Port Automation matches: UniFi's vendor IDs, or custom MAC OUIs ("aa:bb:cc").</summary>
public class UniFiPortAutomationMatch
{
    [JsonPropertyName("vendor_ids")]
    [JsonConverter(typeof(FlexibleScalarStringListConverter))]
    public List<string>? VendorIds { get; set; }

    [JsonPropertyName("mac_ouis")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string>? MacOuis { get; set; }
}
