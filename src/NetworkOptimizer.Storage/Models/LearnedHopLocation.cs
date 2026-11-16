using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// The strongest place a site's own traces gave a hop's address: from its router hostname or a hop a few ms
/// away. A later trace that places the address only by GeoLite2, or not at all, shows this instead. One row per
/// site that saw it, in the main database: every site uses it unless two sites learned different cities.
/// </summary>
public class LearnedHopLocation
{
    [Key]
    public int Id { get; set; }

    /// <summary>The site whose trace placed it.</summary>
    [MaxLength(64)]
    public string SiteSlug { get; set; } = "";

    /// <summary>The hop's address, IPv4 or IPv6, in its canonical text form.</summary>
    [MaxLength(45)]
    public string Address { get; set; } = "";

    /// <summary>The city's name as shown ("Kansas City").</summary>
    [MaxLength(120)]
    public string City { get; set; } = "";

    /// <summary>ISO 3166-1 alpha-2 country code, upper case.</summary>
    [MaxLength(2)]
    public string CountryCode { get; set; } = "";

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>What placed it: 1 for the router's hostname, 2 for a hop a few ms away (the web layer's HopLocationSource).</summary>
    public int Source { get; set; }

    /// <summary>How that trace placed it, as its location tooltip said.</summary>
    [MaxLength(300)]
    public string Detail { get; set; } = "";

    /// <summary>When a trace last placed it this way, UTC.</summary>
    public DateTime SeenAt { get; set; }
}
