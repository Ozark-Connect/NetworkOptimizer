using System.Text.Json;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Default wall materials for the Signal Map floor plan editor's Draw Layout: the building's outline
/// material until a floor has its outline, then the building's last interior material.
/// </summary>
public static class FloorPlanWallMaterials
{
    /// <summary>Default outline material when a building has none picked or saved.</summary>
    public const string DefaultExterior = "exterior_residential";

    /// <summary>Default interior material when a building has none picked.</summary>
    public const string DefaultInterior = "drywall";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class Wall
    {
        public List<Point> Points { get; set; } = new();
        public string Material { get; set; } = DefaultInterior;
        public List<string>? Materials { get; set; }
    }

    private sealed class Point
    {
        public double Lat { get; set; }
        public double Lng { get; set; }
    }

    /// <summary>
    /// True when the floor has its outline: any closed shape, whatever its material, so a metal shed
    /// or a wood outbuilding counts. Until then, drawing stays on the building's outline material so an
    /// outline drawn in pieces keeps it.
    /// </summary>
    public static bool HasOutline(string? wallsJson) => Parse(wallsJson).Any(IsClosed);

    /// <summary>
    /// The building's outline material: the one picked this session, else the most common outline
    /// material across its saved floors, else residential exterior.
    /// </summary>
    public static string BuildingOutlineMaterial(string? picked, IEnumerable<string?> floorWallsJson)
    {
        if (!string.IsNullOrEmpty(picked)) return picked;
        return floorWallsJson
            .Select(OutlineMaterial)
            .OfType<string>()
            .GroupBy(m => m)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? DefaultExterior;
    }

    /// <summary>
    /// The exterior material for a new building on a site: whichever of residential and commercial
    /// exterior is more common on the site's existing outlines, residential on a tie or with neither.
    /// </summary>
    public static string SiteExteriorMaterial(IEnumerable<string?> siteFloorWallsJson)
    {
        int residential = 0, commercial = 0;
        foreach (var json in siteFloorWallsJson)
        {
            var outline = Parse(json).Where(IsClosed).OrderByDescending(Area).FirstOrDefault();
            if (outline == null) continue;
            foreach (var m in SegmentMaterials(outline))
            {
                if (m.Equals("exterior_commercial", StringComparison.OrdinalIgnoreCase)) commercial++;
                else if (m.Equals("exterior_residential", StringComparison.OrdinalIgnoreCase)) residential++;
            }
        }
        return commercial > residential ? "exterior_commercial" : DefaultExterior;
    }

    /// <summary>A floor's outline (its largest closed shape) as lat/lng points, or null when it has none.</summary>
    public static List<(double Lat, double Lng)>? OutlinePoints(string? wallsJson) =>
        Parse(wallsJson).Where(IsClosed).OrderByDescending(Area)
            .Select(w => w.Points.Take(w.Points.Count - 1).Select(p => (p.Lat, p.Lng)).ToList())
            .FirstOrDefault();

    /// <summary>
    /// A floor's shape to fit an import onto: its outline, or when editing has opened it (deleting
    /// one outline wall splits the loop), the convex hull of all its wall points. Null with fewer
    /// than three points.
    /// </summary>
    public static List<(double Lat, double Lng)>? OutlineOrHull(string? wallsJson)
    {
        var outline = OutlinePoints(wallsJson);
        if (outline is { Count: >= 3 }) return outline;
        var pts = Parse(wallsJson).SelectMany(w => w.Points)
            .Select(p => new NetworkOptimizer.Web.Services.InnerSpace.Vec2(p.Lng, p.Lat));
        // A hull is unchanged by the lat/lng scaling, so it can be taken in degrees.
        var hull = NetworkOptimizer.Web.Services.InnerSpace.PlanGeometry.ConvexHull(pts);
        return hull.Count >= 3 ? hull.Select(v => (v.Y, v.X)).ToList() : null;
    }

    /// <summary>The material Draw Layout starts on for a floor.</summary>
    public static string DrawStartMaterial(string? floorWallsJson, string buildingOutline, string? lastInterior) =>
        HasOutline(floorWallsJson) ? (lastInterior ?? DefaultInterior) : buildingOutline;

    /// <summary>
    /// True when a save closes the floor's first outline: drawing then switches to the building's
    /// last interior material.
    /// </summary>
    public static bool OutlineJustClosed(string? wallsJsonBefore, string? wallsJsonAfter) =>
        !HasOutline(wallsJsonBefore) && HasOutline(wallsJsonAfter);

    // A floor's outline is its largest closed shape (interior rooms can be closed too); its material
    // is the shape's most common segment material, so a door or window cut into it does not count.
    private static string? OutlineMaterial(string? wallsJson) =>
        Parse(wallsJson)
            .Where(IsClosed)
            .OrderByDescending(Area)
            .Select(w => SegmentMaterials(w).GroupBy(m => m).OrderByDescending(g => g.Count()).First().Key)
            .FirstOrDefault();

    private static bool IsClosed(Wall w) =>
        w.Points.Count >= 4
        && Math.Abs(w.Points[0].Lat - w.Points[^1].Lat) < 1e-9
        && Math.Abs(w.Points[0].Lng - w.Points[^1].Lng) < 1e-9;

    private static double Area(Wall w)
    {
        double a = 0;
        for (var i = 0; i < w.Points.Count - 1; i++)
            a += w.Points[i].Lng * w.Points[i + 1].Lat - w.Points[i + 1].Lng * w.Points[i].Lat;
        return Math.Abs(a) / 2;
    }

    private static List<Wall> Parse(string? wallsJson)
    {
        if (string.IsNullOrEmpty(wallsJson) || wallsJson == "[]") return new();
        try { return JsonSerializer.Deserialize<List<Wall>>(wallsJson, JsonOpts) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static IEnumerable<string> SegmentMaterials(Wall w)
    {
        for (var i = 0; i < w.Points.Count - 1; i++)
            yield return w.Materials != null && i < w.Materials.Count && !string.IsNullOrEmpty(w.Materials[i])
                ? w.Materials[i]
                : w.Material;
    }
}
