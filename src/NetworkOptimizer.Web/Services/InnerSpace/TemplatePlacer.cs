using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services.InnerSpace;

/// <summary>Where a template is laid down: the map point of its center and a clockwise rotation.</summary>
public sealed record Placement(double Lat, double Lng, double RotationDeg);

/// <summary>Converts a template's local meters to map coordinates and fits it to an existing outline.</summary>
public static class TemplatePlacer
{
    private const double MetersPerDegree = 111_320;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The map point of a local point. Local y is up on the plan, which is north at rotation 0.</summary>
    public static (double Lat, double Lng) ToLatLng(Vec2 local, Placement p)
    {
        var v = local.Rotate(-p.RotationDeg * Math.PI / 180);
        return (p.Lat + v.Y / MetersPerDegree,
                p.Lng + v.X / (MetersPerDegree * Math.Cos(p.Lat * Math.PI / 180)));
    }

    /// <summary>A map point in meters east and north of an origin.</summary>
    public static Vec2 ToLocal(double lat, double lng, double lat0, double lng0) =>
        new((lng - lng0) * MetersPerDegree * Math.Cos(lat0 * Math.PI / 180), (lat - lat0) * MetersPerDegree);

    /// <summary>The floor's walls in the editor's <c>WallsJson</c> shape, with per-segment spans.</summary>
    public static string WallsJson(ImportFloor floor, Placement p)
    {
        var walls = floor.Walls.Where(w => w.Points.Count >= 2 && w.Materials.Count == w.Points.Count - 1).Select(w =>
        {
            var spans = w.Spans.Any(s => s != null)
                ? w.Spans.Select(s => s == null ? null : new SpanDto(s.Bottom, s.Top)).ToList()
                : null;
            var uniform = w.Materials.Distinct(StringComparer.Ordinal).Count() == 1;
            return new WallDto(
                w.Points.Select(pt => { var (lat, lng) = ToLatLng(pt, p); return new PointDto(lat, lng); }).ToList(),
                w.Materials[0],
                uniform && spans == null ? null : w.Materials.ToList(),
                spans);
        }).ToList();
        return JsonSerializer.Serialize(walls, JsonOpts);
    }

    /// <summary>Image overlay bounds (unrotated) and its clockwise rotation, as the editor stores them.</summary>
    public static (double SwLat, double SwLng, double NeLat, double NeLng, double RotationDeg) ImageBounds(ImportImage img, Placement p)
    {
        var (lat, lng) = ToLatLng(img.Center, p);
        var halfLat = img.HeightM / 2 / MetersPerDegree;
        var halfLng = img.WidthM / 2 / (MetersPerDegree * Math.Cos(lat * Math.PI / 180));
        var rot = ((p.RotationDeg + img.RotationDeg) % 360 + 360) % 360;
        return (lat - halfLat, lng - halfLng, lat + halfLat, lng + halfLng, rot);
    }

    /// <summary>The floor's bounds on the map: its walls, APs and image, for the floor row.</summary>
    public static (double SwLat, double SwLng, double NeLat, double NeLng) FloorBounds(ImportFloor floor, Placement p)
    {
        var pts = floor.Walls.SelectMany(w => w.Points).Concat(floor.Aps.Select(a => a.Position)).ToList();
        if (floor.Image != null)
        {
            var c = floor.Image.Center;
            var hw = floor.Image.WidthM / 2;
            var hh = floor.Image.HeightM / 2;
            pts.AddRange(new[] { c + new Vec2(-hw, -hh), c + new Vec2(hw, hh), c + new Vec2(-hw, hh), c + new Vec2(hw, -hh) }
                .Select(q => (q - c).Rotate(-floor.Image.RotationDeg * Math.PI / 180) + c));
        }
        if (pts.Count == 0) pts.Add(default);
        var ll = pts.Select(q => ToLatLng(q, p)).ToList();
        return (ll.Min(x => x.Lat), ll.Min(x => x.Lng), ll.Max(x => x.Lat), ll.Max(x => x.Lng));
    }

    /// <summary>AP orientation on the map after the template's rotation.</summary>
    public static int Orientation(ImportAp ap, Placement p) =>
        (int)Math.Round(((ap.OrientationDeg + p.RotationDeg) % 360 + 360) % 360) % 360;

    /// <summary>
    /// The placement that best lays <paramref name="footprint"/> (local, template frame) over an
    /// existing building outline: area centroids matched, area principal axes aligned, the best of
    /// the four quarter turns by mean distance of evenly spaced edge points. Area-based on purpose:
    /// window and door cuts cluster corners on one side, and vertex averages then pull the fit
    /// metres off and tilt it.
    /// </summary>
    public static Placement FitToOutline(IReadOnlyList<Vec2> footprint, IReadOnlyList<(double Lat, double Lng)> outline) =>
        FitCandidates(footprint, outline)[0];

    /// <summary>
    /// The four quarter-turn fits of <see cref="FitToOutline"/>, best first. A near-rectangular
    /// building matches itself turned half way round almost as well (a roof-traced outline larger
    /// than the scanned walls scores the two within a few percent), so Snap to Building steps
    /// through these rather than trusting the first.
    /// </summary>
    public static List<Placement> FitCandidates(IReadOnlyList<Vec2> footprint, IReadOnlyList<(double Lat, double Lng)> outline)
    {
        if (footprint.Count < 3 || outline.Count < 3)
            throw new ArgumentException("Both outlines need at least three points.");
        var lat0 = outline.Average(o => o.Lat);
        var lng0 = outline.Average(o => o.Lng);
        var target = outline.Select(o => ToLocal(o.Lat, o.Lng, lat0, lng0)).ToList();
        var tc = PlanGeometry.AreaCentroid(target);
        var fc = PlanGeometry.AreaCentroid(footprint);
        var baseTurn = PlanGeometry.AreaPrincipalAngle(target) - PlanGeometry.AreaPrincipalAngle(footprint);
        var samples = PlanGeometry.Resample(footprint, 0.25);

        var origin = new Placement(lat0, lng0, 0);
        return Enumerable.Range(0, 4)
            .Select(k =>
            {
                var ccw = baseTurn + k * Math.PI / 2;
                var moved = samples.Select(q => (q - fc).Rotate(ccw) + tc).ToList();
                return (score: PlanGeometry.MeanDistanceToEdges(moved, target), ccw);
            })
            .OrderBy(c => c.score)
            .Select(c =>
            {
                // The template center (local origin) lands where -fc goes under that turn.
                var center = (new Vec2(0, 0) - fc).Rotate(c.ccw) + tc;
                var clockwise = ((-c.ccw * 180 / Math.PI) % 360 + 360) % 360;
                var (lat, lng) = ToLatLng(center, origin);
                return new Placement(lat, lng, clockwise);
            })
            .ToList();
    }

    private sealed record PointDto(double Lat, double Lng);
    private sealed record SpanDto(double? Bottom, double? Top);
    private sealed record WallDto(List<PointDto> Points, string Material, List<string>? Materials, List<SpanDto?>? Spans);
}
