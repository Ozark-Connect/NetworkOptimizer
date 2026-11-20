namespace NetworkOptimizer.Web.Services.InnerSpace;

/// <summary>Planar geometry for import templates (local meters).</summary>
public static class PlanGeometry
{
    /// <summary>Absolute area of a polygon (closing point optional).</summary>
    public static double Area(IReadOnlyList<Vec2> poly)
    {
        double a = 0;
        for (var i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return Math.Abs(a) / 2;
    }

    /// <summary>Mean of the distinct vertices.</summary>
    public static Vec2 Centroid(IReadOnlyList<Vec2> pts)
    {
        var list = Distinct(pts);
        if (list.Count == 0) return default;
        return new Vec2(list.Average(p => p.X), list.Average(p => p.Y));
    }

    /// <summary>Even-odd point-in-polygon test.</summary>
    public static bool Contains(IReadOnlyList<Vec2> poly, Vec2 p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y)
                && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Parameter of <paramref name="p"/> projected on a-b (0 at a, 1 at b).</summary>
    public static double ProjectT(Vec2 a, Vec2 b, Vec2 p)
    {
        var d = b - a;
        var len2 = d.X * d.X + d.Y * d.Y;
        return len2 < 1e-12 ? 0 : ((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / len2;
    }

    /// <summary>Distance from <paramref name="p"/> to the infinite line through a-b.</summary>
    public static double DistanceToLine(Vec2 a, Vec2 b, Vec2 p)
    {
        var t = ProjectT(a, b, p);
        return (p - (a + (b - a) * t)).Length;
    }

    /// <summary>Distance from <paramref name="p"/> to the segment a-b.</summary>
    public static double DistanceToSegment(Vec2 a, Vec2 b, Vec2 p)
    {
        var t = Math.Clamp(ProjectT(a, b, p), 0, 1);
        return (p - (a + (b - a) * t)).Length;
    }

    /// <summary>Mean distance from each vertex of <paramref name="poly"/> to the nearest edge of <paramref name="target"/>.</summary>
    public static double MeanDistanceToEdges(IReadOnlyList<Vec2> poly, IReadOnlyList<Vec2> target)
    {
        if (poly.Count == 0 || target.Count < 2) return double.MaxValue;
        double sum = 0;
        foreach (var p in poly)
        {
            var best = double.MaxValue;
            for (var i = 0; i < target.Count; i++)
                best = Math.Min(best, DistanceToSegment(target[i], target[(i + 1) % target.Count], p));
            sum += best;
        }
        return sum / poly.Count;
    }

    /// <summary>Angle in radians of the polygon's principal axis (from the vertex covariance).</summary>
    public static double PrincipalAngle(IReadOnlyList<Vec2> pts)
    {
        var list = Distinct(pts);
        if (list.Count < 2) return 0;
        var c = Centroid(list);
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in list)
        {
            var d = p - c;
            sxx += d.X * d.X;
            syy += d.Y * d.Y;
            sxy += d.X * d.Y;
        }
        return 0.5 * Math.Atan2(2 * sxy, sxx - syy);
    }

    /// <summary>
    /// Centroid of the polygon's area. Unlike <see cref="Centroid"/>, corners clustered on one side
    /// (window and door cuts) do not pull it. Falls back to the vertex mean for a degenerate polygon.
    /// </summary>
    public static Vec2 AreaCentroid(IReadOnlyList<Vec2> poly)
    {
        var list = Distinct(poly);
        double a = 0, cx = 0, cy = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i];
            var q = list[(i + 1) % list.Count];
            var c = p.X * q.Y - q.X * p.Y;
            a += c;
            cx += (p.X + q.X) * c;
            cy += (p.Y + q.Y) * c;
        }
        if (Math.Abs(a) < 1e-9) return Centroid(list);
        return new Vec2(cx / (3 * a), cy / (3 * a));
    }

    /// <summary>Angle in radians of the polygon's principal axis from its second moments of area.</summary>
    public static double AreaPrincipalAngle(IReadOnlyList<Vec2> poly)
    {
        var list = Distinct(poly);
        var c = AreaCentroid(list);
        double ixx = 0, iyy = 0, ixy = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i] - c;
            var q = list[(i + 1) % list.Count] - c;
            var k = p.X * q.Y - q.X * p.Y;
            ixx += (p.Y * p.Y + p.Y * q.Y + q.Y * q.Y) * k;
            iyy += (p.X * p.X + p.X * q.X + q.X * q.X) * k;
            ixy += (p.X * q.Y + 2 * p.X * p.Y + 2 * q.X * q.Y + q.X * p.Y) * k;
        }
        // Sign of the area cancels between the moments; atan2 of the ratios gives the long axis.
        return 0.5 * Math.Atan2(2 * ixy / 24, iyy / 12 - ixx / 12);
    }

    /// <summary>Points every <paramref name="step"/> meters along the closed outline's edges.</summary>
    public static List<Vec2> Resample(IReadOnlyList<Vec2> poly, double step)
    {
        var list = Distinct(poly);
        var result = new List<Vec2>();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            var b = list[(i + 1) % list.Count];
            var len = (b - a).Length;
            var n = Math.Max(1, (int)Math.Ceiling(len / step));
            for (var k = 0; k < n; k++) result.Add(a + (b - a) * (k / (double)n));
        }
        return result;
    }

    /// <summary>Convex hull (monotone chain), counterclockwise, no closing point.</summary>
    public static List<Vec2> ConvexHull(IEnumerable<Vec2> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        var hull = new List<Vec2>();
        static double Cross(Vec2 o, Vec2 a, Vec2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        var lower = hull.Count + 1;
        for (var i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>Axis-aligned bounding box as (min, max).</summary>
    public static (Vec2 Min, Vec2 Max) Bounds(IEnumerable<Vec2> pts)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in pts)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return (new Vec2(minX, minY), new Vec2(maxX, maxY));
    }

    /// <summary>Area of the intersection of two bounding boxes.</summary>
    public static double OverlapArea((Vec2 Min, Vec2 Max) a, (Vec2 Min, Vec2 Max) b)
    {
        var w = Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X);
        var h = Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    private static List<Vec2> Distinct(IReadOnlyList<Vec2> pts)
    {
        var list = pts.ToList();
        if (list.Count > 1 && (list[0] - list[^1]).Length < 1e-9) list.RemoveAt(list.Count - 1);
        return list;
    }
}
