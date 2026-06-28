namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Geometry helpers for framing the floor-plan / signal map to real content.
///
/// Floor and building records are created with placeholder coordinates: a new floor
/// inherits its building's center, and a building created before any AP is placed
/// falls back to the app's default map center. Fitting the map to those raw records
/// drags the view to the placeholder (e.g. a German user's map zooming out to the
/// central-US default). These helpers fit only to coordinates that represent
/// deliberate user content: placed APs, drawn walls, and floors with real content
/// (walls or an uploaded image). Bare placeholder records are excluded.
/// </summary>
public static class MapBoundsHelper
{
    /// <summary>A south-west / north-east lat-lng bounding box.</summary>
    public readonly record struct GeoBounds(double SwLat, double SwLng, double NeLat, double NeLng);

    /// <summary>
    /// A floor's stored overlay bounds plus whether the floor has real user content.
    /// A floor with no walls and no image sits at its building's placeholder center and
    /// must not participate in the map fit.
    /// </summary>
    public readonly record struct FloorRecord(double SwLat, double SwLng, double NeLat, double NeLng, bool HasContent);

    /// <summary>
    /// True if a lat/lng pair is a usable real coordinate rather than an unset default.
    /// Some record paths default to (0, 0) null island; both that and out-of-range
    /// values are rejected.
    /// </summary>
    public static bool IsPositioned(double lat, double lng)
    {
        if (Math.Abs(lat) < 0.0001 && Math.Abs(lng) < 0.0001) return false;
        return lat is >= -90 and <= 90 && lng is >= -180 and <= 180;
    }

    /// <summary>
    /// Bounds of all positioned content for a set of buildings: drawn walls if any exist,
    /// otherwise the overlay bounds of floors that have real content. Bare floors sitting
    /// at their building's placeholder center are excluded. Returns null when there is no
    /// positioned content.
    /// </summary>
    public static GeoBounds? ComputeBuildingBounds(
        IEnumerable<(double Lat, double Lng)> wallPoints,
        IEnumerable<FloorRecord> floors)
    {
        var acc = new BoundsAccumulator();

        foreach (var p in wallPoints)
            if (IsPositioned(p.Lat, p.Lng)) acc.Add(p.Lat, p.Lng);

        // Real, drawn walls are the most reliable signal; prefer them outright.
        if (acc.HasData) return acc.ToBounds();

        foreach (var f in floors)
            if (f.HasContent && IsPositioned(f.SwLat, f.SwLng) && IsPositioned(f.NeLat, f.NeLng))
            {
                acc.Add(f.SwLat, f.SwLng);
                acc.Add(f.NeLat, f.NeLng);
            }

        return acc.HasData ? acc.ToBounds() : null;
    }

    /// <summary>
    /// Bounds spanning placed APs plus positioned building content. APs anchor the frame;
    /// wall / floor content extends it. Placeholder records never participate. Returns null
    /// when nothing positioned is present.
    /// </summary>
    public static GeoBounds? ComputeAllContentBounds(
        IEnumerable<(double Lat, double Lng)> apPoints,
        IEnumerable<(double Lat, double Lng)> wallPoints,
        IEnumerable<FloorRecord> floors)
    {
        var acc = new BoundsAccumulator();

        foreach (var p in apPoints)
            if (IsPositioned(p.Lat, p.Lng)) acc.Add(p.Lat, p.Lng);

        var building = ComputeBuildingBounds(wallPoints, floors);
        if (building.HasValue)
        {
            acc.Add(building.Value.SwLat, building.Value.SwLng);
            acc.Add(building.Value.NeLat, building.Value.NeLng);
        }

        return acc.HasData ? acc.ToBounds() : null;
    }

    private struct BoundsAccumulator
    {
        private double _swLat, _swLng, _neLat, _neLng;
        public bool HasData { get; private set; }

        public void Add(double lat, double lng)
        {
            if (!HasData)
            {
                _swLat = _neLat = lat;
                _swLng = _neLng = lng;
                HasData = true;
                return;
            }
            _swLat = Math.Min(_swLat, lat);
            _swLng = Math.Min(_swLng, lng);
            _neLat = Math.Max(_neLat, lat);
            _neLng = Math.Max(_neLng, lng);
        }

        public readonly GeoBounds ToBounds() => new(_swLat, _swLng, _neLat, _neLng);
    }
}
