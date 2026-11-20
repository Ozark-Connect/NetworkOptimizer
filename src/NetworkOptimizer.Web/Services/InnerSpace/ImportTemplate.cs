namespace NetworkOptimizer.Web.Services.InnerSpace;

/// <summary>A point in a template's local frame: meters, x east, y north, origin at the template center.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);

    /// <summary>Rotates counterclockwise by <paramref name="radians"/>.</summary>
    public Vec2 Rotate(double radians)
    {
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);
        return new Vec2(X * c - Y * s, X * s + Y * c);
    }
}

/// <summary>
/// Vertical extent of a wall segment in meters above the floor. Null on either side means the floor
/// or the ceiling. Kept for a later 3D propagation model; today's model is 2D and ignores it.
/// </summary>
public sealed record WallSpan(double? Bottom, double? Top);

/// <summary>Where a template came from.</summary>
public enum ImportSource
{
    /// <summary>A LiDAR scan from the InnerSpace mobile app.</summary>
    Scan,

    /// <summary>A project brought in from Design Center.</summary>
    DesignCenter,

    /// <summary>A plan drawn in InnerSpace over an uploaded image.</summary>
    Drawn,
}

/// <summary>A wall polyline in local meters, with a material and span per segment.</summary>
public sealed class ImportPolyline
{
    public List<Vec2> Points { get; } = new();

    /// <summary>Materials[i] is the segment from Points[i] to Points[i+1].</summary>
    public List<string> Materials { get; } = new();

    /// <summary>Spans[i] is the segment from Points[i] to Points[i+1]; null = full height.</summary>
    public List<WallSpan?> Spans { get; } = new();

    public bool IsClosed => Points.Count >= 4 && (Points[0] - Points[^1]).Length < 1e-6;
}

/// <summary>An AP placed on an imported floor.</summary>
public sealed class ImportAp
{
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";

    /// <summary>Set for an adopted AP; null for a placeholder (planned AP).</summary>
    public string? Mac { get; init; }

    public string? MountType { get; init; }
    public string? AntennaMode { get; init; }
    public Vec2 Position { get; set; }

    /// <summary>Mounting height in meters when the source has one; null = derive from the mount.</summary>
    public double? HeightM { get; init; }

    /// <summary>Direction in degrees, 0 = up on the plan (north at rotation 0), clockwise.</summary>
    public int OrientationDeg { get; set; }
}

/// <summary>The floor image of an imported floor, in local meters.</summary>
public sealed class ImportImage
{
    /// <summary>Console path of the image (fetched through the console session).</summary>
    public string SourceUrl { get; init; } = "";
    public Vec2 Center { get; set; }
    public double WidthM { get; init; }
    public double HeightM { get; init; }

    /// <summary>Clockwise rotation in degrees relative to the template frame.</summary>
    public double RotationDeg { get; set; }
}

/// <summary>One floor of a template.</summary>
public sealed class ImportFloor
{
    /// <summary>InnerSpace plan id; becomes the floor's source reference for re-import.</summary>
    public string SourcePlanId { get; init; } = "";
    public string Label { get; init; } = "";

    /// <summary>Suggested floor number in the editor's convention (1 = first floor): the stack order, else the scan's story + 1.</summary>
    public int FloorNumber { get; set; }

    /// <summary>Floor-to-ceiling height in meters, when the source has one.</summary>
    public double? HeightM { get; init; }

    /// <summary>Our floor material for the slab under this floor, when the source set a loss.</summary>
    public string? FloorMaterial { get; init; }

    public List<ImportPolyline> Walls { get; } = new();
    public List<ImportAp> Aps { get; } = new();
    public ImportImage? Image { get; set; }

    /// <summary>Closed outlines of this floor's structures, outermost only.</summary>
    public List<List<Vec2>> Footprints { get; } = new();
}

/// <summary>
/// One building, ready to lay down: floors in local meters around the template center.
/// </summary>
public sealed class ImportTemplate
{
    /// <summary>Stable id: the source plan ids joined, plus a part index when a canvas was split.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public ImportSource Source { get; init; }
    public string? DcSourceProjectId { get; init; }
    public DateTime? CreatedAt { get; init; }

    /// <summary>True when a newer import of the same Design Center project exists.</summary>
    public bool IsEarlierImport { get; set; }

    /// <summary>True when this template is one part of a canvas or stack that was split.</summary>
    public bool WasSplit { get; init; }

    /// <summary>
    /// Shared by a split plan's parts and its whole: the import screen shows them as one entry and
    /// imports either the parts one by one or, with Import as one building, the whole.
    /// </summary>
    public string? GroupId { get; init; }

    /// <summary>True for the unsplit alternative of a split plan (offered behind Import as one building).</summary>
    public bool IsWhole { get; init; }

    /// <summary>Map pin from the source, used to pre-fill the drop point.</summary>
    public (double Lat, double Lng)? Location { get; init; }

    public List<ImportFloor> Floors { get; } = new();

    /// <summary>Warnings for the import screen (missing scale, no outline found, ...).</summary>
    public List<string> Warnings { get; } = new();

    public int WallSegmentCount => Floors.Sum(f => f.Walls.Sum(w => w.Materials.Count));
    public int ApCount => Floors.Sum(f => f.Aps.Count);
}
