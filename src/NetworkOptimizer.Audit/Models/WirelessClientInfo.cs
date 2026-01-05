using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Wrapper for a wireless client with detection results for audit analysis
/// </summary>
public class WirelessClientInfo
{
    /// <summary>
    /// The UniFi client response data
    /// </summary>
    public required UniFiClientResponse Client { get; init; }

    /// <summary>
    /// The network this client is connected to
    /// </summary>
    public NetworkInfo? Network { get; init; }

    /// <summary>
    /// Device type detection result
    /// </summary>
    public required DeviceDetectionResult Detection { get; init; }

    /// <summary>
    /// Name of the access point this client is connected to
    /// </summary>
    public string? AccessPointName { get; init; }

    /// <summary>
    /// MAC address of the access point
    /// </summary>
    public string? AccessPointMac { get; init; }

    /// <summary>
    /// Model of the access point (e.g., "U6-Pro")
    /// </summary>
    public string? AccessPointModel { get; init; }

    /// <summary>
    /// Friendly model name of the access point (e.g., "UniFi 6 Pro")
    /// </summary>
    public string? AccessPointModelName { get; init; }

    /// <summary>
    /// Display name for the client (name, hostname, product, category, or MAC)
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Client.Name) ? Client.Name :
        !string.IsNullOrWhiteSpace(Client.Hostname) ? Client.Hostname :
        !string.IsNullOrWhiteSpace(Detection.ProductName) ? Detection.ProductName :
        Detection.Category != Core.Enums.ClientDeviceCategory.Unknown ? Detection.CategoryName :
        !string.IsNullOrWhiteSpace(Client.Mac) ? Client.Mac : "Unknown";

    /// <summary>
    /// MAC address of the client
    /// </summary>
    public string? Mac => Client.Mac;

    /// <summary>
    /// WiFi band (2.4GHz, 5GHz, 6GHz) derived from radio type or channel
    /// </summary>
    public string? WifiBand
    {
        get
        {
            // Try to determine from radio type first
            // UniFi uses "na" for 5GHz (802.11a/n/ac) and "ng" for 2.4GHz (802.11b/g/n)
            if (!string.IsNullOrEmpty(Client.Radio))
            {
                var bandFromRadio = Client.Radio.ToLowerInvariant() switch
                {
                    "na" => "5 GHz",
                    "ng" => "2.4 GHz",
                    "6e" or "ax-6e" => "6 GHz",
                    _ => (string?)null
                };
                if (bandFromRadio != null)
                    return bandFromRadio;
                // Fall through to channel detection if radio type unrecognized
            }

            // Fallback to channel-based detection
            if (Client.Channel.HasValue)
            {
                var channel = Client.Channel.Value;
                if (channel >= 1 && channel <= 14)
                    return "2.4 GHz";
                if (channel >= 36 && channel <= 177)
                    return "5 GHz";
                if (channel >= 181 && channel <= 233)
                    return "6 GHz";
            }

            return null;
        }
    }
}
