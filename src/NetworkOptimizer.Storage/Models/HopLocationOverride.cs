using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// The city a person set for a traced hop's address, used in place of the location the engine reads
/// from the router's hostname or GeoLite2. Install-wide: every trace on every site shows it.
/// </summary>
public class HopLocationOverride
{
    [Key]
    public int Id { get; set; }

    /// <summary>The hop's address, IPv4 or IPv6, in its canonical text form.</summary>
    [MaxLength(45)]
    public string Address { get; set; } = "";

    /// <summary>The city's name as shown ("San Antonio").</summary>
    [MaxLength(120)]
    public string City { get; set; } = "";

    /// <summary>The state or province, when the geocoder gives one ("Texas").</summary>
    [MaxLength(120)]
    public string? Region { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code, upper case.</summary>
    [MaxLength(2)]
    public string CountryCode { get; set; } = "";

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>The name of the person who set it.</summary>
    [MaxLength(200)]
    public string? SetBy { get; set; }

    public DateTime SetAt { get; set; }
}
