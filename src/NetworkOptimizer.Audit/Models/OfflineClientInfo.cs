using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Wrapper for an offline client (from history API) with detection results for audit analysis
/// </summary>
public class OfflineClientInfo
{
    /// <summary>
    /// The UniFi client history response data
    /// </summary>
    public required UniFiClientDetailResponse HistoryClient { get; init; }

    /// <summary>
    /// The network this client was last connected to
    /// </summary>
    public NetworkInfo? LastNetwork { get; init; }

    /// <summary>
    /// Device type detection result
    /// </summary>
    public required DeviceDetectionResult Detection { get; init; }

    /// <summary>
    /// Display name for the client (name, hostname, product, category, or MAC)
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(HistoryClient.DisplayName) ? HistoryClient.DisplayName :
        !string.IsNullOrWhiteSpace(HistoryClient.Name) ? HistoryClient.Name :
        !string.IsNullOrWhiteSpace(HistoryClient.Hostname) ? HistoryClient.Hostname :
        !string.IsNullOrWhiteSpace(Detection.ProductName) ? Detection.ProductName :
        Detection.Category != Core.Enums.ClientDeviceCategory.Unknown ? Detection.CategoryName :
        !string.IsNullOrWhiteSpace(HistoryClient.Mac) ? HistoryClient.Mac : "Unknown";

    /// <summary>
    /// MAC address of the client
    /// </summary>
    public string? Mac => HistoryClient.Mac;

    /// <summary>
    /// Whether this is a wired or wireless device
    /// </summary>
    public bool IsWired => HistoryClient.IsWired;

    /// <summary>
    /// Last seen timestamp (Unix epoch seconds)
    /// </summary>
    public long LastSeen => HistoryClient.LastSeen;

    /// <summary>
    /// Last seen as DateTime
    /// </summary>
    public DateTime LastSeenDateTime => DateTimeOffset.FromUnixTimeSeconds(LastSeen).UtcDateTime;

    /// <summary>
    /// Time since the device was last seen
    /// </summary>
    public TimeSpan TimeSinceLastSeen => DateTime.UtcNow - LastSeenDateTime;

    /// <summary>
    /// Whether the device was seen within the last 2 weeks (affects scoring)
    /// Devices seen within 2 weeks get full score impact, older devices are Info only
    /// </summary>
    public bool IsRecentlyActive => TimeSinceLastSeen.TotalDays <= 14;

    /// <summary>
    /// Name of the AP or switch the client was last connected to
    /// </summary>
    public string? LastUplinkName => HistoryClient.LastUplinkName;

    /// <summary>
    /// Model name of the AP or switch the client was last connected to (e.g., "UniFi 6 Pro")
    /// </summary>
    public string? LastUplinkModelName { get; init; }

    /// <summary>
    /// Friendly display of how long ago the device was seen
    /// </summary>
    public string LastSeenDisplay
    {
        get
        {
            var span = TimeSinceLastSeen;
            if (span.TotalMinutes < 60)
                return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalHours < 24)
                return $"{(int)span.TotalHours} hr ago";
            if (span.TotalDays < 7)
                return $"{(int)span.TotalDays} days ago";
            return $"{(int)(span.TotalDays / 7)} weeks ago";
        }
    }
}
