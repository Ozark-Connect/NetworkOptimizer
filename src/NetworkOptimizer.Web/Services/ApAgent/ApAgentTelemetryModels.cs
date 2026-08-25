using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// The AP Agent's GET /clients reply, reduced to what the collector consumes. The agent serves
/// snake_case, so every name is declared rather than inferred.
/// </summary>
public sealed class ApAgentClientsPayload
{
    /// <summary>The agent's clock when it built the reply.</summary>
    [JsonPropertyName("collected_at")]
    public DateTime CollectedAt { get; set; }

    /// <summary>How each collection tier on the AP last fared.</summary>
    [JsonPropertyName("sources")]
    public ApAgentTierStatus? Sources { get; set; }

    /// <summary>One entry per client, already resolved across MLO links by the agent.</summary>
    [JsonPropertyName("clients")]
    public List<ApAgentClient> Clients { get; set; } = new();
}

/// <summary>The three-tier collection health the agent reports on every payload.</summary>
public sealed class ApAgentTierStatus
{
    /// <summary>The hostapd event stream.</summary>
    [JsonPropertyName("events")]
    public ApAgentTierInfo? Events { get; set; }

    /// <summary>The wlanconfig sweep.</summary>
    [JsonPropertyName("fast")]
    public ApAgentTierInfo? Fast { get; set; }

    /// <summary>The mca-dump pass, which carries the quality fields.</summary>
    [JsonPropertyName("slow")]
    public ApAgentTierInfo? Slow { get; set; }
}

/// <summary>One collection tier's last outcome on the AP.</summary>
public sealed class ApAgentTierInfo
{
    /// <summary>Whether the tier's underlying tool resolved at all.</summary>
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    /// <summary>When the tier last completed a pass.</summary>
    [JsonPropertyName("last_collected_at")]
    public DateTime? LastCollectedAt { get; set; }
}

/// <summary>
/// One client as the agent resolved it. An MLO client is ONE entry keyed on its MLD MAC, and the
/// scalar fields describe the active link, so the collector must never re-derive either from
/// <see cref="Links"/>.
/// </summary>
public sealed class ApAgentClient
{
    /// <summary>The MLD MAC for an MLO client, the station MAC otherwise.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>Same value as <see cref="Key"/>, in the agent's own client-facing spelling.</summary>
    [JsonPropertyName("mac")]
    public string Mac { get; set; } = "";

    /// <summary>Present only on an MLO client.</summary>
    [JsonPropertyName("mld_mac")]
    public string? MldMac { get; set; }

    /// <summary>Whether the client negotiated multi-link operation.</summary>
    [JsonPropertyName("is_mlo")]
    public bool IsMlo { get; set; }

    /// <summary>Active link's band, as the agent's "2.4" / "5" / "6" token.</summary>
    [JsonPropertyName("band")]
    public string? Band { get; set; }

    /// <summary>Active link's channel.</summary>
    [JsonPropertyName("channel")]
    public int Channel { get; set; }

    /// <summary>Active link's channel width in MHz.</summary>
    [JsonPropertyName("bw")]
    public int Bandwidth { get; set; }

    /// <summary>Active link's signal in dBm.</summary>
    [JsonPropertyName("signal")]
    public int? Signal { get; set; }

    /// <summary>Active link's noise floor in dBm.</summary>
    [JsonPropertyName("noise")]
    public int? Noise { get; set; }

    /// <summary>Active link's signal-to-noise ratio in dB.</summary>
    [JsonPropertyName("snr")]
    public int? Snr { get; set; }

    /// <summary>Active link's transmit rate in kbps (AP to client).</summary>
    [JsonPropertyName("tx_rate_kbps")]
    public long TxRateKbps { get; set; }

    /// <summary>Active link's receive rate in kbps (client to AP).</summary>
    [JsonPropertyName("rx_rate_kbps")]
    public long RxRateKbps { get; set; }

    /// <summary>The AP's own satisfaction score for the client.</summary>
    [JsonPropertyName("satisfaction")]
    public int? Satisfaction { get; set; }

    /// <summary>What the client can do, as opposed to what it is doing.</summary>
    [JsonPropertyName("capabilities")]
    public ApAgentClientCapabilities? Capabilities { get; set; }

    /// <summary>Every association behind this client. One entry unless the client is MLO.</summary>
    [JsonPropertyName("links")]
    public List<ApAgentClientLink> Links { get; set; } = new();
}

/// <summary>The capability bits the AP reports for a client.</summary>
public sealed class ApAgentClientCapabilities
{
    /// <summary>Maximum spatial streams the client advertises.</summary>
    [JsonPropertyName("nss")]
    public int Nss { get; set; }
}

/// <summary>
/// One association. The counters the additive fields come from live here rather than on the client,
/// so the collector reads them off the active link.
/// </summary>
public sealed class ApAgentClientLink
{
    /// <summary>Whether this is the link carrying traffic.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    /// <summary>This link's band token.</summary>
    [JsonPropertyName("band")]
    public string? Band { get; set; }

    /// <summary>Operating spatial streams on this link.</summary>
    [JsonPropertyName("nss")]
    public int Nss { get; set; }

    /// <summary>Client connection quality, as the AP scores it.</summary>
    [JsonPropertyName("ccq")]
    public int Ccq { get; set; }

    /// <summary>Cumulative bytes the AP transmitted to the client.</summary>
    [JsonPropertyName("tx_bytes")]
    public long TxBytes { get; set; }

    /// <summary>Cumulative bytes the AP received from the client.</summary>
    [JsonPropertyName("rx_bytes")]
    public long RxBytes { get; set; }

    /// <summary>Cumulative transmit retries.</summary>
    [JsonPropertyName("tx_retries")]
    public long TxRetries { get; set; }

    /// <summary>Cumulative transmit attempts.</summary>
    [JsonPropertyName("wifi_tx_attempts")]
    public long TxAttempts { get; set; }

    /// <summary>Cumulative frames the AP gave up on.</summary>
    [JsonPropertyName("wifi_tx_dropped")]
    public long TxDropped { get; set; }

    /// <summary>The AP's moving transmit latency, in microseconds.</summary>
    [JsonPropertyName("wifi_tx_latency_mov")]
    public ApAgentTxLatency? TxLatency { get; set; }

    /// <summary>TCP quality on the AP-to-client direction.</summary>
    [JsonPropertyName("tx_tcp_stats")]
    public ApAgentTcpStats? TxTcpStats { get; set; }
}

/// <summary>The AP's moving transmit latency window, in microseconds.</summary>
public sealed class ApAgentTxLatency
{
    /// <summary>Mean transmit latency over the window.</summary>
    [JsonPropertyName("avg")]
    public int Avg { get; set; }

    /// <summary>Worst transmit latency over the window.</summary>
    [JsonPropertyName("max")]
    public int Max { get; set; }
}

/// <summary>The AP's TCP observations for one direction.</summary>
public sealed class ApAgentTcpStats
{
    /// <summary>Mean round-trip latency in milliseconds.</summary>
    [JsonPropertyName("lat_avg")]
    public int LatAvg { get; set; }

    /// <summary>Cumulative stalled-connection count.</summary>
    [JsonPropertyName("stalls")]
    public int Stalls { get; set; }
}

/// <summary>The AP Agent's GET /radios reply, reduced to what the collector keeps.</summary>
public sealed class ApAgentRadiosPayload
{
    /// <summary>The agent's clock when it built the reply.</summary>
    [JsonPropertyName("collected_at")]
    public DateTime CollectedAt { get; set; }

    /// <summary>One entry per radio.</summary>
    [JsonPropertyName("radios")]
    public List<ApAgentRadio> Radios { get; set; } = new();
}

/// <summary>
/// One radio. The counter maps arrive holding hundreds of entries; only the airtime and wedge
/// counters are retained, and the rest are dropped on parse.
/// </summary>
public sealed class ApAgentRadio
{
    /// <summary>Interface name, e.g. "wifi0".</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>mca-dump's radio token.</summary>
    [JsonPropertyName("radio")]
    public string? Radio { get; set; }

    /// <summary>Band token.</summary>
    [JsonPropertyName("band")]
    public string? Band { get; set; }

    /// <summary>Operating channel.</summary>
    [JsonPropertyName("channel")]
    public int Channel { get; set; }

    /// <summary>Operating width in MHz.</summary>
    [JsonPropertyName("bw")]
    public int Bandwidth { get; set; }

    /// <summary>Measured noise floor in dBm.</summary>
    [JsonPropertyName("noise_floor")]
    public int? NoiseFloor { get; set; }

    /// <summary>Whether this is the dedicated scan radio, which hops channels and serves no clients.</summary>
    [JsonPropertyName("scan_radio")]
    public bool ScanRadio { get; set; }

    /// <summary>Whether this entry exists only to carry counters, with no radio state behind it.</summary>
    [JsonPropertyName("counter_only")]
    public bool CounterOnly { get; set; }

    /// <summary>Raw counters, a union across the radio-stats tools.</summary>
    [JsonPropertyName("counters")]
    public Dictionary<string, long>? Counters { get; set; }

    /// <summary>Counter movement since the agent's previous pass.</summary>
    [JsonPropertyName("counter_deltas")]
    public Dictionary<string, long>? Deltas { get; set; }

    /// <summary>Seconds the deltas span.</summary>
    [JsonPropertyName("delta_seconds")]
    public double DeltaSeconds { get; set; }
}
