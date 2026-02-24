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
    /// Normalize traffic flow entries into ThreatEvent entities.
    /// Expects the full response from the traffic-flows endpoint.
    /// </summary>
    public List<ThreatEvent> NormalizeFlowEvents(JsonElement response)
    {
        var results = new List<ThreatEvent>();

        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var flow in data.EnumerateArray())
        {
            try
            {
                var id = flow.GetPropertyOrDefault("id", "");
                if (string.IsNullOrEmpty(id)) continue;

                var timestamp = flow.GetPropertyOrDefault("time", 0L);

                var sourceIp = "";
                var sourcePort = 0;
                if (flow.TryGetProperty("source", out var src))
                {
                    sourceIp = src.GetPropertyOrDefault("ip", "");
                    sourcePort = src.GetPropertyOrDefault("port", 0);
                }

                var destIp = "";
                var destPort = 0;
                string? domain = null;
                if (flow.TryGetProperty("destination", out var dst))
                {
                    destIp = dst.GetPropertyOrDefault("ip", "");
                    destPort = dst.GetPropertyOrDefault("port", 0);

                    if (dst.TryGetProperty("domains", out var domains) &&
                        domains.ValueKind == JsonValueKind.Array &&
                        domains.GetArrayLength() > 0)
                    {
                        domain = domains[0].GetString();
                    }
                }

                var protocol = flow.GetPropertyOrDefault("protocol", "");
                var action = flow.GetPropertyOrDefault("action", "");
                var risk = flow.GetPropertyOrDefault("risk", "low");
                var direction = flow.GetPropertyOrDefault("direction", "");
                var service = flow.GetPropertyOrDefault("service", "");

                long? bytesTotal = null;
                if (flow.TryGetProperty("traffic_data", out var trafficData))
                {
                    var bt = trafficData.GetPropertyOrDefault("bytes_total", 0L);
                    if (bt > 0) bytesTotal = bt;
                }

                var durationMs = flow.GetPropertyOrDefault("duration_milliseconds", 0L);

                string? networkName = null;
                if (flow.TryGetProperty("source", out var srcForNetwork))
                {
                    var nn = srcForNetwork.GetPropertyOrDefault("network_name", "");
                    if (!string.IsNullOrEmpty(nn)) networkName = nn;
                }

                var severity = MapFlowSeverity(risk, action);
                var threatAction = action.Equals("blocked", StringComparison.OrdinalIgnoreCase)
                    ? ThreatAction.Blocked
                    : ThreatAction.Detected;

                // Extract real IPS signature data when available
                long signatureId = 0;
                var signatureName = $"Flow: {service} {direction} {action}";
                var category = $"{risk} risk {direction} {service}";
                if (flow.TryGetProperty("ips", out var ips) && ips.ValueKind == JsonValueKind.Object)
                {
                    var ipsSignature = ips.GetPropertyOrDefault("signature", "");
                    if (!string.IsNullOrEmpty(ipsSignature))
                    {
                        signatureName = ipsSignature;
                        signatureId = ips.GetPropertyOrDefault("signature_id", 0L);
                        var ipsCategory = ips.GetPropertyOrDefault("category_name", "");
                        if (!string.IsNullOrEmpty(ipsCategory))
                            category = ipsCategory;
                        // Use Suricata severity when available (inner_alert_severity maps from the ips.advanced_information)
                        var ipsSeverityStr = ips.GetPropertyOrDefault("advanced_information", "");
                        if (ipsSeverityStr.StartsWith("IPS Alert ", StringComparison.OrdinalIgnoreCase) &&
                            ipsSeverityStr.Length > 10 && char.IsDigit(ipsSeverityStr[10]))
                        {
                            var suricataSeverity = ipsSeverityStr[10] - '0';
                            if (suricataSeverity is >= 1 and <= 4)
                                severity = NormalizeSeverity(suricataSeverity);
                        }
                    }
                }

                var threatEvent = new ThreatEvent
                {
                    InnerAlertId = $"flow-{id}",
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime,
                    SourceIp = sourceIp,
                    SourcePort = sourcePort,
                    DestIp = destIp,
                    DestPort = destPort,
                    Protocol = protocol,
                    SignatureId = signatureId,
                    SignatureName = signatureName,
                    Category = category,
                    Severity = severity,
                    Action = threatAction,
                    EventSource = EventSource.TrafficFlow,
                    Domain = domain,
                    Direction = direction,
                    Service = service,
                    BytesTotal = bytesTotal,
                    FlowDurationMs = durationMs > 0 ? durationMs : null,
                    NetworkName = networkName,
                    RiskLevel = risk
                };

                results.Add(threatEvent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to normalize traffic flow entry");
            }
        }

        return results;
    }

    /// <summary>
    /// Map flow risk + action to our 1-5 severity scale.
    /// </summary>
    internal static int MapFlowSeverity(string risk, string action)
    {
        var isBlocked = action.Equals("blocked", StringComparison.OrdinalIgnoreCase);
        return risk.ToLowerInvariant() switch
        {
            "high" when isBlocked => 5,
            "high" => 4,
            "medium" when isBlocked => 4,
            "medium" => 3,
            "low" when isBlocked => 2,
            _ => 1
        };
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
