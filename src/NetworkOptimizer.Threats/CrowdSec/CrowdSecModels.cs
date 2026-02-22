using System.Text.Json;
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

    [JsonPropertyName("reputation")]
    public string? Reputation { get; set; }

    [JsonPropertyName("background_noise_score")]
    public int BackgroundNoiseScore { get; set; }

    [JsonPropertyName("confidence")]
    public JsonElement? Confidence { get; set; }

    [JsonPropertyName("behaviors")]
    public List<CrowdSecBehavior> Behaviors { get; set; } = [];

    [JsonPropertyName("attack_details")]
    public List<CrowdSecAttackDetail> AttackDetails { get; set; } = [];

    [JsonPropertyName("scores")]
    public CrowdSecScores? Scores { get; set; }

    [JsonPropertyName("location")]
    public CrowdSecLocation? Location { get; set; }

    [JsonPropertyName("as_name")]
    public string? AsName { get; set; }

    [JsonPropertyName("as_num")]
    public int AsNum { get; set; }

    [JsonPropertyName("classifications")]
    public CrowdSecClassifications? Classifications { get; set; }

    [JsonPropertyName("history")]
    public CrowdSecHistory? History { get; set; }

    [JsonPropertyName("mitre_techniques")]
    public List<CrowdSecMitreTechnique> MitreTechniques { get; set; } = [];
}

public class CrowdSecConfidence
{
    [JsonPropertyName("overall")]
    public string? Overall { get; set; }

    [JsonPropertyName("last_day")]
    public double LastDay { get; set; }

    [JsonPropertyName("last_week")]
    public double LastWeek { get; set; }

    [JsonPropertyName("last_month")]
    public double LastMonth { get; set; }
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
