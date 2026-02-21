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

    // --- Geo/ASN enrichment ---
    public string? CountryCode { get; set; }
    public string? City { get; set; }
    public int? Asn { get; set; }
    public string? AsnOrg { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>
    /// Kill chain classification assigned by the classifier.
    /// </summary>
    public KillChainStage KillChainStage { get; set; }

    /// <summary>
    /// FK to a detected ThreatPattern if this event is part of one.
    /// </summary>
    public int? PatternId { get; set; }
    public ThreatPattern? Pattern { get; set; }
}
