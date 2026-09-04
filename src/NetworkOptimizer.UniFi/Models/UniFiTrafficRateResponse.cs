using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// One bucket of POST v2/api/site/{site}/app-traffic-rate: a client's (or the site's) WAN traffic
/// over <see cref="IntervalSeconds"/>, as UniFi Network's DPI saw it. Directions are the client's
/// own: <c>rx_byte-r</c> is the average download rate in bytes per second over the bucket.
/// Every window asked for so far came back in 5-minute buckets.
/// </summary>
[VendorSpecific("UniFi", "v2/api/site/{site}/app-traffic-rate")]
public class UniFiTrafficRateBucket
{
    [JsonPropertyName("timestamp")]
    public long TimestampMs { get; set; }

    [JsonPropertyName("interval_seconds")]
    public int IntervalSeconds { get; set; }

    /// <summary>Average bytes per second the client received over the bucket.</summary>
    [JsonPropertyName("rx_byte-r")]
    public double RxBytesPerSecond { get; set; }

    /// <summary>Average bytes per second the client sent over the bucket.</summary>
    [JsonPropertyName("tx_byte-r")]
    public double TxBytesPerSecond { get; set; }

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    public DateTime Time => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs).UtcDateTime;

    /// <summary>Bytes the client received over the bucket.</summary>
    public long DownloadBytes => (long)Math.Round(RxBytesPerSecond * IntervalSeconds);

    /// <summary>Bytes the client sent over the bucket.</summary>
    public long UploadBytes => (long)Math.Round(TxBytesPerSecond * IntervalSeconds);
}
