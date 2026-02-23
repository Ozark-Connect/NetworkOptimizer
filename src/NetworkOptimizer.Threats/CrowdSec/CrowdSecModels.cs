using System.Text.Json.Serialization;

namespace NetworkOptimizer.Threats.CrowdSec;

/// <summary>
/// Response from CrowdSec CTI Smoke API: GET /v2/smoke/{ip}
/// </summary>
public class CrowdSecIpInfo
{
    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("ip_range")]
    public string? IpRange { get; set; }

    [JsonPropertyName("ip_range_score")]
    public int? IpRangeScore { get; set; }

    [JsonPropertyName("ip_range_24")]
    public string? IpRange24 { get; set; }

    [JsonPropertyName("ip_range_24_reputation")]
    public string? IpRange24Reputation { get; set; }

    [JsonPropertyName("ip_range_24_score")]
    public int? IpRange24Score { get; set; }

    [JsonPropertyName("reputation")]
    public string? Reputation { get; set; }

    [JsonPropertyName("background_noise_score")]
    public int? BackgroundNoiseScore { get; set; }

    [JsonPropertyName("background_noise")]
    public string? BackgroundNoise { get; set; }

    [JsonPropertyName("confidence")]
    public string? Confidence { get; set; }

    [JsonPropertyName("reverse_dns")]
    public string? ReverseDns { get; set; }

    [JsonPropertyName("behaviors")]
    public List<CrowdSecBehavior> Behaviors { get; set; } = [];

    [JsonPropertyName("attack_details")]
    public List<CrowdSecAttackDetail> AttackDetails { get; set; } = [];

    [JsonPropertyName("references")]
    public List<CrowdSecReference> References { get; set; } = [];

    [JsonPropertyName("cves")]
    public List<string> Cves { get; set; } = [];

    [JsonPropertyName("scores")]
    public CrowdSecScores? Scores { get; set; }

    [JsonPropertyName("location")]
    public CrowdSecLocation? Location { get; set; }

    [JsonPropertyName("as_name")]
    public string? AsName { get; set; }

    [JsonPropertyName("as_num")]
    public int? AsNum { get; set; }

    [JsonPropertyName("classifications")]
    public CrowdSecClassifications? Classifications { get; set; }

    [JsonPropertyName("history")]
    public CrowdSecHistory? History { get; set; }

    [JsonPropertyName("mitre_techniques")]
    public List<CrowdSecMitreTechnique> MitreTechniques { get; set; } = [];

    [JsonPropertyName("target_countries")]
    public Dictionary<string, double>? TargetCountries { get; set; }
}

public class CrowdSecBehavior
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public class CrowdSecAttackDetail
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("references")]
    public List<string> References { get; set; } = [];
}

public class CrowdSecReference
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public class CrowdSecScores
{
    [JsonPropertyName("overall")]
    public CrowdSecScoreBreakdown? Overall { get; set; }

    [JsonPropertyName("last_day")]
    public CrowdSecScoreBreakdown? LastDay { get; set; }

    [JsonPropertyName("last_week")]
    public CrowdSecScoreBreakdown? LastWeek { get; set; }

    [JsonPropertyName("last_month")]
    public CrowdSecScoreBreakdown? LastMonth { get; set; }
}

public class CrowdSecScoreBreakdown
{
    [JsonPropertyName("aggressiveness")]
    public int Aggressiveness { get; set; }

    [JsonPropertyName("threat")]
    public int Threat { get; set; }

    [JsonPropertyName("trust")]
    public int Trust { get; set; }

    [JsonPropertyName("anomaly")]
    public int Anomaly { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public class CrowdSecLocation
{
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
}

public class CrowdSecClassifications
{
    [JsonPropertyName("false_positives")]
    public List<CrowdSecClassification> FalsePositives { get; set; } = [];

    [JsonPropertyName("classifications")]
    public List<CrowdSecClassification> Items { get; set; } = [];
}

public class CrowdSecClassification
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public class CrowdSecHistory
{
    [JsonPropertyName("first_seen")]
    public string? FirstSeen { get; set; }

    [JsonPropertyName("last_seen")]
    public string? LastSeen { get; set; }

    [JsonPropertyName("full_age")]
    public int FullAge { get; set; }

    [JsonPropertyName("days_age")]
    public int DaysAge { get; set; }
}

public class CrowdSecMitreTechnique
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
