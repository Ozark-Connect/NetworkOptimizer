using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// UniFi Network's own unauthenticated <c>GET /status</c> on its local port (8081 on a console).
/// Only the two fields that say the application is up are mapped.
/// </summary>
[VendorSpecific("UniFi", "Network app /status on its local port, not through /proxy/network")]
public class UniFiNetworkStatus
{
    [JsonPropertyName("meta")]
    public UniFiNetworkStatusMeta? Meta { get; set; }

    /// <summary>True when Network answers rc ok and reports its server running.</summary>
    [JsonIgnore]
    public bool IsRunning =>
        string.Equals(Meta?.Rc, "ok", StringComparison.OrdinalIgnoreCase) && Meta?.ServerRunning == true;
}

/// <summary>The meta block of <see cref="UniFiNetworkStatus"/>.</summary>
[VendorSpecific("UniFi", "Network app /status meta block")]
public class UniFiNetworkStatusMeta
{
    [JsonPropertyName("rc")]
    public string? Rc { get; set; }

    /// <summary>False while Network is still starting.</summary>
    [JsonPropertyName("server_running")]
    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? ServerRunning { get; set; }
}
