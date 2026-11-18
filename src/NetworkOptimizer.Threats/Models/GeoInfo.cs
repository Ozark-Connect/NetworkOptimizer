namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// Geo/ASN enrichment data for a source IP address.
/// </summary>
public record GeoInfo
{
    public string? CountryCode { get; init; }
    public string? City { get; init; }
    public int? Asn { get; init; }
    public string? AsnOrg { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// <summary>GeoLite2's accuracy radius for the location, in km: about 1,000 when only the country is known.</summary>
    public int? AccuracyRadiusKm { get; init; }

    public static GeoInfo Empty => new();
}
