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

    public static GeoInfo Empty => new();
}
