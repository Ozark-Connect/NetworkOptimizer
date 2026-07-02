using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A persisted AP radio channel-change record. Sourced from the UniFi Console's system log
/// (which only retains a few days) plus our own observed config diffs, so the Channel
/// Recommendation engine can attribute long-term metrics to the config that was actually live
/// and suppress hop-back recommendations while a fresh channel change is still soaking.
/// </summary>
public class ApChannelChange
{
    [Key]
    public int Id { get; set; }

    /// <summary>AP MAC address (lowercase, colon-separated)</summary>
    [Required]
    [MaxLength(17)]
    public string ApMac { get; set; } = "";

    /// <summary>Radio band code - "ng" (2.4 GHz), "na" (5 GHz), "6e" (6 GHz)</summary>
    [Required]
    [MaxLength(10)]
    public string Band { get; set; } = "";

    /// <summary>Channel before the change; null for the first observation of this radio</summary>
    public int? PreviousChannel { get; set; }

    /// <summary>Channel width (MHz) before the change; null when unknown</summary>
    public int? PreviousWidthMhz { get; set; }

    /// <summary>Channel after the change</summary>
    public int NewChannel { get; set; }

    /// <summary>Channel width (MHz) after the change; null when unknown</summary>
    public int? NewWidthMhz { get; set; }

    /// <summary>When the change occurred (UTC). For observed diffs this is detection time,
    /// which upper-bounds the real change time.</summary>
    public DateTime ChangedAtUtc { get; set; }

    /// <summary>Where the record came from (see <see cref="ApChannelChangeSource"/>)</summary>
    [Required]
    [MaxLength(20)]
    public string Source { get; set; } = "";
}

/// <summary>
/// Well-known <see cref="ApChannelChange.Source"/> values.
/// </summary>
public static class ApChannelChangeSource
{
    /// <summary>Parsed from a UniFi system-log channel-change event (authoritative timestamp)</summary>
    public const string UniFiEvent = "unifi-event";

    /// <summary>Detected by comparing the live radio config against the last known config</summary>
    public const string Observed = "observed";

    /// <summary>First sighting of this radio - establishes the baseline config, no prior channel</summary>
    public const string Initial = "initial";
}
