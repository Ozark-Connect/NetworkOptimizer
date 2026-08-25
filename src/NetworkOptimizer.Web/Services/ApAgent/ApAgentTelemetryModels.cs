using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// The AP Agent's GET /clients reply, reduced to what the collector consumes. The agent serves
/// snake_case, so every name is declared rather than inferred.
/// </summary>
public sealed class ApAgentClientsPayload
{
    /// <summary>Which access point answered.</summary>
    [JsonPropertyName("ap")]
    public ApAgentApInfo? Ap { get; set; }

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

/// <summary>Which access point a payload came from.</summary>
public sealed class ApAgentApInfo
{
    /// <summary>The access point's own hostname.</summary>
    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    /// <summary>Model token as the access point reports it.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>The access point's MAC.</summary>
    [JsonPropertyName("mac")]
    public string? Mac { get; set; }
}

/// <summary>
/// The AP Agent's GET /client/&lt;mac&gt; reply. The agent resolves a link MAC to its parent client,
/// so a Wi-Fi 7 client is found by any of its link MACs and comes back as one record.
/// </summary>
public sealed class ApAgentClientPayload
{
    /// <summary>Which access point answered.</summary>
    [JsonPropertyName("ap")]
    public ApAgentApInfo? Ap { get; set; }

    /// <summary>The agent's clock when it built the reply.</summary>
    [JsonPropertyName("collected_at")]
    public DateTime CollectedAt { get; set; }

    /// <summary>How each collection tier on the AP last fared.</summary>
    [JsonPropertyName("sources")]
    public ApAgentTierStatus? Sources { get; set; }

    /// <summary>The client, already resolved across MLO links by the agent.</summary>
    [JsonPropertyName("client")]
    public ApAgentClient? Client { get; set; }
}

/// <summary>The membership event kinds the agent publishes. These strings are its contract.</summary>
public static class ApAgentEventTypes
{
    /// <summary>A client joined this access point.</summary>
    public const string Assoc = "assoc";

    /// <summary>A client left this access point.</summary>
    public const string Disassoc = "disassoc";

    /// <summary>This access point announced that a client is moving to a peer.</summary>
    public const string RoamBroadcast = "roam_broadcast";

    /// <summary>A peer told this access point that a client moved.</summary>
    public const string RoamToPeer = "roam_to_peer";
}

/// <summary>One membership fact from an access point's hostapd control socket.</summary>
public sealed class ApAgentEvent
{
    /// <summary>Position in the agent's ring, which restarts at 1 when the agent restarts.</summary>
    [JsonPropertyName("seq")]
    public ulong Seq { get; set; }

    /// <summary>One of <see cref="ApAgentEventTypes"/>, or a kind this server does not model.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>
    /// The VAP the event arrived on. It is the only thing that resolves an event to a BSSID, band,
    /// and channel, none of which the event itself carries.
    /// </summary>
    [JsonPropertyName("vap")]
    public string? Vap { get; set; }

    /// <summary>The client the event is about. On an MLO client this is the link MAC, not the MLD MAC.</summary>
    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    /// <summary>The rest of the control-socket line, kept verbatim.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>The source's own timestamp, where the source provides one. hostapd does not.</summary>
    [JsonPropertyName("event_time")]
    public DateTime? EventTime { get; set; }

    /// <summary>The BSSID the client is moving to, on a roam event.</summary>
    [JsonPropertyName("peer_bssid")]
    public string? PeerBssid { get; set; }

    /// <summary>When the agent recorded it.</summary>
    [JsonPropertyName("collected_at")]
    public DateTime CollectedAt { get; set; }

    /// <summary>The access-point-side instant, preferring the source's own clock where it has one.</summary>
    [JsonIgnore]
    public DateTime At => (EventTime ?? CollectedAt).ToUniversalTime();
}

/// <summary>The AP Agent's GET /events?since= reply, a bounded replay window.</summary>
public sealed class ApAgentEventsPayload
{
    /// <summary>Which access point answered.</summary>
    [JsonPropertyName("ap")]
    public ApAgentApInfo? Ap { get; set; }

    /// <summary>True when the window the caller asked for had already been overwritten.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    /// <summary>The retained events, oldest first.</summary>
    [JsonPropertyName("events")]
    public List<ApAgentEvent> Events { get; set; } = new();

    /// <summary>
    /// When the agent process started. The ring holds no state across a restart, so a change here
    /// means sequence numbering began again at 1 and a stored cursor no longer applies to it.
    /// </summary>
    [JsonPropertyName("agent_started_at")]
    public DateTime AgentStartedAt { get; set; }

    /// <summary>The replay window's shape, which is how an undersized ring becomes visible.</summary>
    [JsonPropertyName("ring")]
    public ApAgentRingStats? Ring { get; set; }

    /// <summary>The agent's clock when it built the reply.</summary>
    [JsonPropertyName("collected_at")]
    public DateTime CollectedAt { get; set; }
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
    /// <summary>This link's own station MAC, which differs per link on an MLO client.</summary>
    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    /// <summary>Whether this is the link carrying traffic.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    /// <summary>This link's channel.</summary>
    [JsonPropertyName("channel")]
    public int Channel { get; set; }

    /// <summary>This link's channel width in MHz.</summary>
    [JsonPropertyName("bw")]
    public int Bandwidth { get; set; }

    /// <summary>This link's signal in dBm.</summary>
    [JsonPropertyName("signal")]
    public int? Signal { get; set; }

    /// <summary>This link's noise floor in dBm.</summary>
    [JsonPropertyName("noise")]
    public int? Noise { get; set; }

    /// <summary>This link's signal-to-noise ratio in dB.</summary>
    [JsonPropertyName("snr")]
    public int? Snr { get; set; }

    /// <summary>This link's transmit rate in kbps (AP to client).</summary>
    [JsonPropertyName("tx_rate_kbps")]
    public long TxRateKbps { get; set; }

    /// <summary>This link's receive rate in kbps (client to AP).</summary>
    [JsonPropertyName("rx_rate_kbps")]
    public long RxRateKbps { get; set; }

    /// <summary>The driver's phy-mode token, e.g. "IEEE80211_MODE_11AXA_HE160".</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>The AP's satisfaction score for this link.</summary>
    [JsonPropertyName("satisfaction")]
    public int? Satisfaction { get; set; }

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
