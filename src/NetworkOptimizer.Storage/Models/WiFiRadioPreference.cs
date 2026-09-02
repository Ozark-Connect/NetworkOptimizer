using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// What the operator told the Channel Recommendation engine about one AP radio. Today that is
/// Keep: hold the radio on its current channel rather than recommending a move.
/// </summary>
public class WiFiRadioPreference
{
    [Key]
    public int Id { get; set; }

    /// <summary>AP MAC address (lowercase, colon-separated).</summary>
    [Required]
    [MaxLength(17)]
    public string ApMac { get; set; } = "";

    /// <summary>Radio band code: "ng" (2.4 GHz), "na" (5 GHz), "6e" (6 GHz).</summary>
    [Required]
    [MaxLength(10)]
    public string Band { get; set; } = "";

    /// <summary>When Keep was set (UTC); null when the radio is not kept.</summary>
    public DateTime? KeepChannelSince { get; set; }

    /// <summary>Last write (UTC).</summary>
    public DateTime UpdatedAt { get; set; }
}
