using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// One page of <c>POST v2/api/site/{site}/traffic-flows</c>, the Network app's flow history
/// (Insights - Flows). Shape read from Network 11.1; a console that does not serve it answers 405.
/// </summary>
public class UniFiTrafficFlowPage
{
    [JsonPropertyName("data")]
    public List<UniFiTrafficFlow> Data { get; set; } = new();

    [JsonPropertyName("has_next")]
    public bool HasNext { get; set; }

    [JsonPropertyName("total_element_count")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? TotalElementCount { get; set; }
}

/// <summary>
/// One flow from the Network app's flow history. Times are Unix milliseconds. The client is the
/// <see cref="Source"/> for an outgoing flow; ports are absent on ICMP.
/// </summary>
[NetworkOptimizer.Core.VendorSpecific("UniFi", "v2 traffic-flows record shape, Network 11.1")]
public class UniFiTrafficFlow
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("risk")]
    public string? Risk { get; set; }

    [JsonPropertyName("flow_start_time")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long FlowStartTime { get; set; }

    [JsonPropertyName("flow_end_time")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long FlowEndTime { get; set; }

    /// <summary>When the console last updated this record; the history window filters on it.</summary>
    [JsonPropertyName("time")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long Time { get; set; }

    [JsonPropertyName("duration_milliseconds")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long DurationMilliseconds { get; set; }

    /// <summary>Both directions together: the console does not split them.</summary>
    [JsonPropertyName("traffic_data")]
    public UniFiTrafficFlowData? TrafficData { get; set; }

    [JsonPropertyName("application")]
    public UniFiTrafficFlowApplication? Application { get; set; }

    [JsonPropertyName("source")]
    public UniFiTrafficFlowEndpoint? Source { get; set; }

    [JsonPropertyName("destination")]
    public UniFiTrafficFlowEndpoint? Destination { get; set; }

    [JsonPropertyName("in")]
    public UniFiTrafficFlowNetwork? In { get; set; }

    [JsonPropertyName("out")]
    public UniFiTrafficFlowNetwork? Out { get; set; }

    [JsonPropertyName("policies")]
    public List<UniFiTrafficFlowPolicy>? Policies { get; set; }
}

public class UniFiTrafficFlowData
{
    [JsonPropertyName("bytes_total")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long BytesTotal { get; set; }

    [JsonPropertyName("packets_total")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long PacketsTotal { get; set; }
}

public class UniFiTrafficFlowApplication
{
    [JsonPropertyName("app")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? App { get; set; }

    [JsonPropertyName("cat")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Category { get; set; }
}

public class UniFiTrafficFlowEndpoint
{
    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("port")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Port { get; set; }

    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    [JsonPropertyName("client_name")]
    public string? ClientName { get; set; }

    [JsonPropertyName("host_name")]
    public string? HostName { get; set; }

    [JsonPropertyName("domains")]
    public List<string>? Domains { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("network_name")]
    public string? NetworkName { get; set; }

    [JsonPropertyName("zone_name")]
    public string? ZoneName { get; set; }
}

public class UniFiTrafficFlowNetwork
{
    [JsonPropertyName("network_id")]
    public string? NetworkId { get; set; }

    [JsonPropertyName("network_name")]
    public string? NetworkName { get; set; }
}

public class UniFiTrafficFlowPolicy
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("internal_type")]
    public string? InternalType { get; set; }
}
