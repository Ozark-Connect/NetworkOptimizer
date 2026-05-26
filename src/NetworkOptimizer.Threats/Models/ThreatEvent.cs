namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// Normalized IPS/IDS event entity. Each row represents one alert from the UniFi gateway's
/// threat management system, enriched with geo/ASN data and classified into a kill chain stage.
/// </summary>
public class ThreatEvent
{
    public int Id { get; set; }

    /// <summary>
    /// When the event occurred (UTC).
    /// </summary>
    public DateTime Timestamp { get; set; }

    public string SourceIp { get; set; } = string.Empty;
    public int SourcePort { get; set; }
    public string DestIp { get; set; } = string.Empty;
    public int DestPort { get; set; }
    public string Protocol { get; set; } = string.Empty;

    /// <summary>
    /// Suricata signature ID (SID).
    /// </summary>
    public long SignatureId { get; set; }

    /// <summary>
    /// Human-readable signature name.
    /// </summary>
    public string SignatureName { get; set; } = string.Empty;

    /// <summary>
    /// Suricata category (e.g., "Attempted Information Leak", "A Network Trojan was Detected").
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Severity 1-5 (1 = lowest, 5 = critical). Mapped from Suricata severity.
    /// </summary>
    public int Severity { get; set; }

    /// <summary>
    /// Whether the IPS blocked or only detected this event.
    /// </summary>
    public ThreatAction Action { get; set; }

    /// <summary>
    /// UniFi _id for deduplication across syncs.
    /// </summary>
    public string InnerAlertId { get; set; } = string.Empty;

    // --- Source IP geo/ASN enrichment ---
    // These reflect the SOURCE IP. For RFC1918 sources, all fields remain null.
    public string? CountryCode { get; set; }
    public string? City { get; set; }
    public int? Asn { get; set; }
    public string? AsnOrg { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    // --- Destination IP geo/ASN enrichment ---
    // These reflect the DEST IP. Populated for traffic-flow events where the
    // external endpoint is the destination. Kept separate from the source fields
    // so source-IP grouping (Top Threat Sources) does not display destination
    // ASNs as if they belonged to the source.
    public string? DestCountryCode { get; set; }
    public string? DestCity { get; set; }
    public int? DestAsn { get; set; }
    public string? DestAsnOrg { get; set; }
    public double? DestLatitude { get; set; }
    public double? DestLongitude { get; set; }

    /// <summary>
    /// True once geo enrichment has been attempted on this event. Drives the
    /// backfill loop's predicate so RFC1918 events (which will always have null
    /// source geo) are not re-processed forever.
    /// </summary>
    public bool GeoEnriched { get; set; }

    /// <summary>
    /// Kill chain classification assigned by the classifier.
    /// </summary>
    public KillChainStage KillChainStage { get; set; }

    // --- Traffic flow fields (nullable - only populated for EventSource.TrafficFlow) ---

    /// <summary>
    /// Which API produced this event.
    /// </summary>
    public EventSource EventSource { get; set; }

    /// <summary>
    /// Destination domain from traffic flows (e.g., "api.cloudflare.com").
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Flow direction ("incoming" / "outgoing").
    /// </summary>
    public string? Direction { get; set; }

    /// <summary>
    /// Service label from traffic flows (e.g., "HTTPS", "DNS", "SSH").
    /// </summary>
    public string? Service { get; set; }

    /// <summary>
    /// Total traffic bytes for the flow.
    /// </summary>
    public long? BytesTotal { get; set; }

    /// <summary>
    /// Duration of the flow in milliseconds.
    /// </summary>
    public long? FlowDurationMs { get; set; }

    /// <summary>
    /// Source network name from UniFi.
    /// </summary>
    public string? NetworkName { get; set; }

    /// <summary>
    /// Raw risk level from UniFi ("low", "medium", "high").
    /// </summary>
    public string? RiskLevel { get; set; }

    /// <summary>
    /// FK to a detected ThreatPattern if this event is part of one.
    /// </summary>
    public int? PatternId { get; set; }
    public ThreatPattern? Pattern { get; set; }
}
