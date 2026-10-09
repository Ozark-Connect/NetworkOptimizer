using System.Text.Json.Serialization;

namespace NetworkOptimizer.Threats.Waf;

/// <summary>
/// One page from netopt-waf's GET /api/events (NetworkOptimizer-Proxy).
/// </summary>
public sealed class WafEventPage
{
    /// <summary>Changes on every WAF restart; a cursor from another instance is void.</summary>
    [JsonPropertyName("instance")] public string Instance { get; set; } = "";
    [JsonPropertyName("started")] public DateTimeOffset Started { get; set; }
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("paranoia")] public int Paranoia { get; set; }
    [JsonPropertyName("stats")] public WafStats Stats { get; set; } = new();
    /// <summary>Sequence number to pass as since= on the next call.</summary>
    [JsonPropertyName("next")] public ulong Next { get; set; }
    [JsonPropertyName("events")] public List<WafEvent> Events { get; set; } = [];
}

/// <summary>Counters since the WAF process started.</summary>
public sealed class WafStats
{
    [JsonPropertyName("inspected")] public long Inspected { get; set; }
    [JsonPropertyName("blocked")] public long Blocked { get; set; }
    [JsonPropertyName("detected")] public long Detected { get; set; }
}

/// <summary>A request that crossed the OWASP CRS anomaly threshold.</summary>
public sealed class WafEvent
{
    [JsonPropertyName("seq")] public ulong Seq { get; set; }
    [JsonPropertyName("ts")] public DateTimeOffset Time { get; set; }
    [JsonPropertyName("src_ip")] public string SourceIp { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
    /// <summary>"blocked" or "detected".</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("anomaly_score")] public int AnomalyScore { get; set; }
    /// <summary>CRS rules that fired, most severe first.</summary>
    [JsonPropertyName("rules")] public List<WafRuleHit> Rules { get; set; } = [];
}

/// <summary>One CRS rule that fired on a request.</summary>
public sealed class WafRuleHit
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("msg")] public string Message { get; set; } = "";
    /// <summary>CRS severity: CRITICAL, ERROR, WARNING, or NOTICE.</summary>
    [JsonPropertyName("severity")] public string Severity { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
}

/// <summary>WAF events stored for a time range.</summary>
public sealed class WafSummary
{
    public int Total { get; set; }
    public int Blocked { get; set; }
    public int Detected { get; set; }
    public List<WafRuleCount> TopRules { get; set; } = [];
    public List<WafHostCount> TopHosts { get; set; } = [];
}

/// <summary>How often one CRS rule led a WAF event.</summary>
public sealed record WafRuleCount(long RuleId, string Message, int Count);

/// <summary>WAF events against one proxied hostname.</summary>
public sealed record WafHostCount(string Host, int Count);

/// <summary>
/// The WAF as of the last poll, for the Threat Intelligence card.
/// </summary>
public sealed record WafStatus(
    DateTimeOffset PolledAt,
    string? Mode,
    int Paranoia,
    DateTimeOffset? Started,
    WafStats? Stats,
    string? Error);
