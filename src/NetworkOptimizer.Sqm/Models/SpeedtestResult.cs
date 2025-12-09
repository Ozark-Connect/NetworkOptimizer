using System.Text.Json.Serialization;

namespace NetworkOptimizer.Sqm.Models;

/// <summary>
/// Ookla Speedtest JSON result
/// </summary>
public class SpeedtestResult
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("ping")]
    public PingInfo Ping { get; set; } = new();

    [JsonPropertyName("download")]
    public BandwidthInfo Download { get; set; } = new();

    [JsonPropertyName("upload")]
    public BandwidthInfo Upload { get; set; } = new();

    [JsonPropertyName("packetLoss")]
    public double PacketLoss { get; set; }

    [JsonPropertyName("isp")]
    public string Isp { get; set; } = string.Empty;

    [JsonPropertyName("interface")]
    public InterfaceInfo Interface { get; set; } = new();

    [JsonPropertyName("server")]
    public ServerInfo Server { get; set; } = new();

    [JsonPropertyName("result")]
    public ResultInfo Result { get; set; } = new();
}

public class PingInfo
{
    [JsonPropertyName("jitter")]
    public double Jitter { get; set; }

    [JsonPropertyName("latency")]
    public double Latency { get; set; }

    [JsonPropertyName("low")]
    public double Low { get; set; }

    [JsonPropertyName("high")]
    public double High { get; set; }
}

public class BandwidthInfo
{
    /// <summary>
    /// Bandwidth in bytes per second
    /// </summary>
    [JsonPropertyName("bandwidth")]
    public long Bandwidth { get; set; }

    /// <summary>
    /// Bytes transferred
    /// </summary>
    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    /// <summary>
    /// Elapsed time in milliseconds
    /// </summary>
    [JsonPropertyName("elapsed")]
    public int Elapsed { get; set; }

    /// <summary>
    /// Latency info during test
    /// </summary>
    [JsonPropertyName("latency")]
    public LatencyInfo? Latency { get; set; }
}

public class LatencyInfo
{
    [JsonPropertyName("iqm")]
    public double Iqm { get; set; }

    [JsonPropertyName("low")]
    public double Low { get; set; }

    [JsonPropertyName("high")]
    public double High { get; set; }

    [JsonPropertyName("jitter")]
    public double Jitter { get; set; }
}

public class InterfaceInfo
{
    [JsonPropertyName("internalIp")]
    public string InternalIp { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("macAddr")]
    public string MacAddr { get; set; } = string.Empty;

    [JsonPropertyName("isVpn")]
    public bool IsVpn { get; set; }

    [JsonPropertyName("externalIp")]
    public string ExternalIp { get; set; } = string.Empty;
}

public class ServerInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;
}

public class ResultInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("persisted")]
    public bool Persisted { get; set; }
}
