using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats;

/// <summary>
/// Normalizes IPS events from different UniFi API formats into ThreatEvent entities.
/// </summary>
public class ThreatEventNormalizer
{
    private readonly ILogger<ThreatEventNormalizer> _logger;

    public ThreatEventNormalizer(ILogger<ThreatEventNormalizer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Normalize v1 stat/ips/event responses.
    /// </summary>
    public List<ThreatEvent> NormalizeV1Events(JsonElement eventsArray)
    {
        var results = new List<ThreatEvent>();
        if (eventsArray.ValueKind != JsonValueKind.Array) return results;

        foreach (var evt in eventsArray.EnumerateArray())
        {
            try
            {
                var id = evt.GetPropertyOrDefault("_id", "");
                if (string.IsNullOrEmpty(id)) continue;

                var timestamp = evt.GetPropertyOrDefault("timestamp", 0L);
                var alert = evt.TryGetProperty("alert", out var alertProp) ? alertProp : default;

                var threatEvent = new ThreatEvent
                {
                    InnerAlertId = id,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime,
                    SourceIp = evt.GetPropertyOrDefault("src_ip", ""),
                    SourcePort = evt.GetPropertyOrDefault("src_port", 0),
                    DestIp = evt.GetPropertyOrDefault("dest_ip", ""),
                    DestPort = evt.GetPropertyOrDefault("dest_port", 0),
                    Protocol = evt.GetPropertyOrDefault("proto", ""),
                    SignatureId = alert.ValueKind != JsonValueKind.Undefined ? alert.GetPropertyOrDefault("signature_id", 0L) : 0,
                    SignatureName = alert.ValueKind != JsonValueKind.Undefined ? alert.GetPropertyOrDefault("signature", "") : "",
                    Category = alert.ValueKind != JsonValueKind.Undefined
                        ? alert.GetPropertyOrDefault("category", evt.GetPropertyOrDefault("catname", ""))
                        : evt.GetPropertyOrDefault("catname", ""),
                    Severity = alert.ValueKind != JsonValueKind.Undefined ? NormalizeSeverity(alert.GetPropertyOrDefault("severity", 3)) : 3,
                    Action = NormalizeAction(alert.ValueKind != JsonValueKind.Undefined ? alert.GetPropertyOrDefault("action", "") : "")
                };

                results.Add(threatEvent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to normalize v1 IPS event");
            }
        }

        return results;
    }

    /// <summary>
    /// Normalize v2 system-log threat management responses.
    /// </summary>
    public List<ThreatEvent> NormalizeV2Events(JsonElement response)
    {
        var results = new List<ThreatEvent>();

        // v2 response: { "data": [...], "totalCount": N, "isLastPage": bool }
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var entry in data.EnumerateArray())
        {
            try
            {
                var id = entry.GetPropertyOrDefault("_id", "");
                if (string.IsNullOrEmpty(id)) continue;

                var timestamp = entry.GetPropertyOrDefault("time", entry.GetPropertyOrDefault("timestamp", 0L));

                var threatEvent = new ThreatEvent
                {
                    InnerAlertId = id,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime,
                    SourceIp = entry.GetPropertyOrDefault("src_ip", ""),
                    SourcePort = entry.GetPropertyOrDefault("src_port", 0),
                    DestIp = entry.GetPropertyOrDefault("dst_ip", entry.GetPropertyOrDefault("dest_ip", "")),
                    DestPort = entry.GetPropertyOrDefault("dst_port", entry.GetPropertyOrDefault("dest_port", 0)),
                    Protocol = entry.GetPropertyOrDefault("proto", ""),
                    SignatureId = entry.GetPropertyOrDefault("inner_alert_signature_id", 0L),
                    SignatureName = entry.GetPropertyOrDefault("inner_alert_signature", entry.GetPropertyOrDefault("msg", "")),
                    Category = entry.GetPropertyOrDefault("inner_alert_category", entry.GetPropertyOrDefault("category_name", "")),
                    Severity = NormalizeSeverity(entry.GetPropertyOrDefault("inner_alert_severity", 3)),
                    Action = NormalizeAction(entry.GetPropertyOrDefault("inner_alert_action", ""))
                };

                results.Add(threatEvent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to normalize v2 threat log entry");
            }
        }

        return results;
    }

    /// <summary>
    /// Normalize Suricata severity (1=high, 2=medium, 3=low) to our 1-5 scale.
    /// Suricata uses inverted severity: 1 is most severe, 4 is least.
    /// </summary>
    internal static int NormalizeSeverity(int suricataSeverity)
    {
        return suricataSeverity switch
        {
            1 => 5, // Suricata high -> our critical
            2 => 4, // Suricata medium -> our high
            3 => 2, // Suricata low -> our low
            4 => 1, // Suricata info -> our info
            _ => 3  // Default to medium
        };
    }

    /// <summary>
    /// Normalize IPS action string to our ThreatAction enum.
    /// </summary>
    internal static ThreatAction NormalizeAction(string action)
    {
        return action.ToLowerInvariant() switch
        {
            "drop" or "reject" or "blocked" => ThreatAction.Blocked,
            "alert" or "pass" or "allowed" or "detected" => ThreatAction.Detected,
            _ => ThreatAction.Detected // Default to detected (less severe)
        };
    }
}

/// <summary>
/// Extension methods for safe JSON property access.
/// </summary>
internal static class JsonElementExtensions
{
    public static string GetPropertyOrDefault(this JsonElement element, string propertyName, string defaultValue)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (element.TryGetProperty(propertyName, out var prop))
        {
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? defaultValue : prop.ToString();
        }
        return defaultValue;
    }

    public static int GetPropertyOrDefault(this JsonElement element, string propertyName, int defaultValue)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (element.TryGetProperty(propertyName, out var prop) && prop.TryGetInt32(out var value))
            return value;
        return defaultValue;
    }

    public static long GetPropertyOrDefault(this JsonElement element, string propertyName, long defaultValue)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (element.TryGetProperty(propertyName, out var prop) && prop.TryGetInt64(out var value))
            return value;
        return defaultValue;
    }
}
