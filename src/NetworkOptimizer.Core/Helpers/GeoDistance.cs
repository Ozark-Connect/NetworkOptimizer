namespace NetworkOptimizer.Core.Helpers;

/// <summary>Great-circle distance between two points on Earth.</summary>
public static class GeoDistance
{
    /// <summary>Mean Earth radius, in meters.</summary>
    public const double EarthRadiusMeters = 6_371_000;

    /// <summary>Haversine distance in meters between two latitude/longitude points, in degrees.</summary>
    public static double Meters(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLng = (lng2 - lng1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Haversine distance in kilometers.</summary>
    public static double Kilometers(double lat1, double lng1, double lat2, double lng2) =>
        Meters(lat1, lng1, lat2, lng2) / 1000.0;
}
