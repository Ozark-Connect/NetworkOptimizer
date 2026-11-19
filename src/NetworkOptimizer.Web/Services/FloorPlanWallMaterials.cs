using System.Text.Json;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Default wall materials for the Signal Map floor plan editor's Draw Layout: exterior until a floor
/// has its outline, then the building's last interior material.
/// </summary>
public static class FloorPlanWallMaterials
{
    /// <summary>Default exterior type when a building has none picked or saved.</summary>
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

    /// <summary>True for any exterior wall type, including the legacy <c>exterior</c> key.</summary>
    public static bool IsExterior(string? material) =>
        material != null && material.StartsWith("exterior", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the floor has a closed shape with at least one exterior segment. Until then, drawing
    /// defaults to the building's exterior type so an outline drawn in pieces keeps its material.
    /// </summary>
    public static bool HasExteriorOutline(string? wallsJson) =>
        Parse(wallsJson).Any(w =>
            w.Points.Count >= 4
            && Math.Abs(w.Points[0].Lat - w.Points[^1].Lat) < 1e-9
            && Math.Abs(w.Points[0].Lng - w.Points[^1].Lng) < 1e-9
            && SegmentMaterials(w).Any(IsExterior));

    /// <summary>
    /// The building's exterior type: the one picked this session, else the most common exterior type
    /// on its saved floors, else residential.
    /// </summary>
    public static string BuildingExterior(string? picked, IEnumerable<string?> floorWallsJson)
    {
        if (!string.IsNullOrEmpty(picked)) return picked;
        return floorWallsJson
            .SelectMany(Parse)
            .SelectMany(SegmentMaterials)
            .Where(m => m is "exterior_residential" or "exterior_commercial")
            .GroupBy(m => m)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? DefaultExterior;
    }

    /// <summary>The material Draw Layout starts on for a floor.</summary>
    public static string DrawStartMaterial(string? floorWallsJson, string buildingExterior, string? lastInterior) =>
        HasExteriorOutline(floorWallsJson) ? (lastInterior ?? DefaultInterior) : buildingExterior;

    /// <summary>
    /// True when a save closes the floor's outline while an exterior material is selected: drawing then
    /// switches to the building's last interior material.
    /// </summary>
    public static bool OutlineJustClosed(string? currentMaterial, string? wallsJsonBefore, string? wallsJsonAfter) =>
        IsExterior(currentMaterial) && !HasExteriorOutline(wallsJsonBefore) && HasExteriorOutline(wallsJsonAfter);

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
