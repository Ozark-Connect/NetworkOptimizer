using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// The replay window's capacity and how much of it has been overwritten. Roam records are built
/// from a sequence cursor, so an undersized ring has to be visible rather than inferred from a gap
/// in the rows.
/// </summary>
public sealed class ApAgentRingStats
{
    /// <summary>How many events the ring holds.</summary>
    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    /// <summary>The oldest sequence still retained.</summary>
    [JsonPropertyName("oldest_seq")]
    public long OldestSeq { get; set; }

    /// <summary>The newest sequence stored.</summary>
    [JsonPropertyName("newest_seq")]
    public long NewestSeq { get; set; }

    /// <summary>Events the ring overwrote over the agent's life.</summary>
    [JsonPropertyName("dropped")]
    public long Dropped { get; set; }
}

/// <summary>The AP Agent's GET /vaps reply, reduced to what resolves a roam's BSSID.</summary>
public sealed class ApAgentVapsPayload
{
    /// <summary>One entry per VAP.</summary>
    [JsonPropertyName("vaps")]
    public List<ApAgentVap> Vaps { get; set; } = new();

    /// <summary>The agent's clock when it built the reply.</summary>
    [JsonPropertyName("collected_at")]
    public DateTime CollectedAt { get; set; }
}

/// <summary>One VAP. Band, channel, and BSSID are only available here, never on a client record.</summary>
public sealed class ApAgentVap
{
    /// <summary>Interface name, e.g. "ath0", which is what an event carries.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Band token.</summary>
    [JsonPropertyName("band")]
    public string? Band { get; set; }

    /// <summary>Operating channel.</summary>
    [JsonPropertyName("channel")]
    public int Channel { get; set; }

    /// <summary>The BSSID clients associate to.</summary>
    [JsonPropertyName("bssid")]
    public string? Bssid { get; set; }

    /// <summary>The broadcast SSID.</summary>
    [JsonPropertyName("essid")]
    public string? Essid { get; set; }
}
