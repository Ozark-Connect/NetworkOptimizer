using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// One hour of Site Health: the share of user-time with good service, and how each area impaired
/// it. Impairments run 0 (none) to 1 (everyone affected for the whole hour); null means no data.
/// Computed in the background from Influx and read by every page, so pages never query Influx for it.
/// </summary>
public class SiteHealthHour
{
    [Key]
    public int Id { get; set; }

    /// <summary>UTC start of the hour.</summary>
    public DateTime HourStart { get; set; }

    /// <summary>0 to 100, or null when the hour had no data at all.</summary>
    public double? Health { get; set; }

    /// <summary>Primary-WAN impairment, ISP and local parts combined.</summary>
    public double? Internet { get; set; }

    /// <summary>The ISP part of <see cref="Internet"/> (outage, loss, latency with no local cause).</summary>
    public double? InternetIsp { get; set; }

    public double? Wifi { get; set; }

    public double? Availability { get; set; }

    public double? Lan { get; set; }

    /// <summary>Wireless share of the hour's clients, which weights <see cref="Wifi"/>.</summary>
    public double? WirelessShare { get; set; }

    /// <summary>Distinct clients seen in the hour, wireless and wired.</summary>
    public int? Clients { get; set; }

    /// <summary>Median loss across the loss pool, percent.</summary>
    public double? LossPct { get; set; }

    /// <summary>Median idle RTT to the access ISP hops, ms. The latency baseline is built from this.</summary>
    public double? AccessRttMs { get; set; }

    /// <summary>Loaded minus idle access RTT, ms, when the WAN was loaded in the hour.</summary>
    public double? LoadedDeltaMs { get; set; }

    /// <summary>Share of the hour the WAN was loaded.</summary>
    public double? LoadedShare { get; set; }

    /// <summary>The largest contributor, for a one-line label.</summary>
    [MaxLength(200)]
    public string? Worst { get; set; }

    /// <summary>The top contributors as JSON (area, impairment, text), for hover.</summary>
    [MaxLength(2000)]
    public string? DetailsJson { get; set; }

    /// <summary>Scorer version that produced the row; older rows are recomputed.</summary>
    public int Version { get; set; }

    public DateTime ComputedAt { get; set; }
}
