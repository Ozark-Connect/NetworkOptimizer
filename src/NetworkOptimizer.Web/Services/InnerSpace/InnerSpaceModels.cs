using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Web.Services.InnerSpace;

/// <summary>
/// Response of InnerSpace's <c>GET /proxy/innerspace/integration/v1/project?mode=2D</c>.
/// </summary>
[VendorSpecific("UniFi", "InnerSpace integration v1 project payload (InnerSpace 1.4.x)")]
public sealed class InnerSpaceProjectResponse
{
    [JsonPropertyName("data")]
    public InnerSpaceProject? Data { get; set; }
}

/// <summary>An InnerSpace project: every floor plan on the console and their shapes.</summary>
[VendorSpecific("UniFi", "InnerSpace integration v1 project payload")]
public sealed class InnerSpaceProject
{
    [JsonPropertyName("plans")]
    public List<InnerSpacePlan> Plans { get; set; } = new();

    /// <summary>Shapes of every plan. Each element is one of the shape types, picked by its <c>type</c>.</summary>
    [JsonPropertyName("shapes")]
    public List<JsonElement> Shapes { get; set; } = new();

    [JsonPropertyName("products")]
    public List<InnerSpaceProduct> Products { get; set; } = new();

    [JsonPropertyName("wallTypes")]
    public List<InnerSpaceWallType> WallTypes { get; set; } = new();
}

/// <summary>One floor plan. <c>Ordering</c> 0 is outside the building; 1..n is the stack order.</summary>
[VendorSpecific("UniFi", "InnerSpace Plan row")]
public sealed class InnerSpacePlan
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "real";
    [JsonPropertyName("ordering")] public int Ordering { get; set; }

    /// <summary>Floor slab loss in dB; null until the floor joins the building.</summary>
    [JsonPropertyName("attenuation")] public double? Attenuation { get; set; }

    [JsonPropertyName("location")] public InnerSpaceLocation? Location { get; set; }
    [JsonPropertyName("siteId")] public string? SiteId { get; set; }
    [JsonPropertyName("dcSourceProjectId")] public string? DcSourceProjectId { get; set; }
    [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
}

/// <summary>A geo plan's map pin.</summary>
public sealed class InnerSpaceLocation
{
    [JsonPropertyName("latitude")] public double Latitude { get; set; }
    [JsonPropertyName("longitude")] public double Longitude { get; set; }
}

/// <summary>A point in a plan's shape frame (image-centered units, y up).</summary>
public sealed class InnerSpacePoint
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("z")] public double? Z { get; set; }
}

/// <summary>A rotation quaternion.</summary>
public sealed class InnerSpaceQuaternion
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("z")] public double Z { get; set; }
    [JsonPropertyName("w")] public double W { get; set; }
}

/// <summary>Floor image and alignment of one plan (<c>type: map</c>).</summary>
[VendorSpecific("UniFi", "InnerSpace MapShape")]
public sealed class InnerSpaceMapShape
{
    [JsonPropertyName("planId")] public string? PlanId { get; set; }
    [JsonPropertyName("urlImage")] public string? UrlImage { get; set; }

    /// <summary>The 3D model; set only on LiDAR scans.</summary>
    [JsonPropertyName("urlModel")] public string? UrlModel { get; set; }

    /// <summary>The scan's archive (holds the RoomPlan capture with windows and doors).</summary>
    [JsonPropertyName("additionalZipUrl")] public string? AdditionalZipUrl { get; set; }

    /// <summary>Alignment offset into the building's first floor, in shape units.</summary>
    [JsonPropertyName("position")] public List<InnerSpacePoint>? Position { get; set; }

    /// <summary>Alignment rotation in radians.</summary>
    [JsonPropertyName("rotation")] public double Rotation { get; set; }

    [JsonPropertyName("imageOffset")] public InnerSpacePoint? ImageOffset { get; set; }
    [JsonPropertyName("imageScale")] public InnerSpacePoint? ImageScale { get; set; }
    [JsonPropertyName("imageRotation")] public double ImageRotation { get; set; }
}

/// <summary>A scale line (<c>type: scale</c>): <c>Scale</c> meters between its two points.</summary>
[VendorSpecific("UniFi", "InnerSpace ScaleShape")]
public sealed class InnerSpaceScaleShape
{
    [JsonPropertyName("planId")] public string? PlanId { get; set; }
    [JsonPropertyName("position")] public List<InnerSpacePoint> Position { get; set; } = new();
    [JsonPropertyName("scale")] public double? Scale { get; set; }

    /// <summary>Wall (floor-to-ceiling) height in meters.</summary>
    [JsonPropertyName("height")] public double? Height { get; set; }

    /// <summary>True for InnerSpace's placeholder line, false for one the author set.</summary>
    [JsonPropertyName("defaultScale")] public bool DefaultScale { get; set; }
}

/// <summary>A wall, door or window segment (<c>type: wall</c>).</summary>
[VendorSpecific("UniFi", "InnerSpace WallShape")]
public sealed class InnerSpaceWallShape
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("planId")] public string? PlanId { get; set; }
    [JsonPropertyName("position")] public List<InnerSpacePoint> Position { get; set; } = new();
    [JsonPropertyName("variant")] public string Variant { get; set; } = "concrete";
    [JsonPropertyName("wallTypeId")] public string? WallTypeId { get; set; }
}

/// <summary>A placed device (<c>type: device</c>).</summary>
[VendorSpecific("UniFi", "InnerSpace DeviceShape")]
public sealed class InnerSpaceDeviceShape
{
    [JsonPropertyName("planId")] public string? PlanId { get; set; }
    [JsonPropertyName("productId")] public string ProductId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("position")] public List<InnerSpacePoint> Position { get; set; } = new();
    [JsonPropertyName("rotation")] public InnerSpaceDeviceRotation? Rotation { get; set; }
    [JsonPropertyName("mount")] public string? Mount { get; set; }
    [JsonPropertyName("antenna")] public string? Antenna { get; set; }
    [JsonPropertyName("meta")] public InnerSpaceDeviceMeta? Meta { get; set; }
}

/// <summary>Mount orientation (<c>base</c>) and antenna direction (<c>pov</c>).</summary>
public sealed class InnerSpaceDeviceRotation
{
    [JsonPropertyName("base")] public InnerSpaceQuaternion? Base { get; set; }
    [JsonPropertyName("pov")] public InnerSpaceQuaternion? Pov { get; set; }
}

/// <summary>Live data on an adopted device; absent on a placeholder.</summary>
public sealed class InnerSpaceDeviceMeta
{
    [JsonPropertyName("mac")] public string? Mac { get; set; }
}

/// <summary>A product a device shape refers to.</summary>
[VendorSpecific("UniFi", "InnerSpace Product row")]
public sealed class InnerSpaceProduct
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sku")] public string Sku { get; set; } = "";
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("subcategory")] public string? Subcategory { get; set; }

    /// <summary>True for an adopted device; false for a placeholder (Design Center or planned).</summary>
    [JsonPropertyName("isManaged")] public bool IsManaged { get; set; }
}

/// <summary>A custom wall type, or an override that renames a built-in material project-wide.</summary>
[VendorSpecific("UniFi", "InnerSpace WallType row")]
public sealed class InnerSpaceWallType
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("attenuation")] public double Attenuation { get; set; }
    [JsonPropertyName("overridesVariant")] public string? OverridesVariant { get; set; }
    [JsonPropertyName("isDeleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("bottomHeight")] public double? BottomHeight { get; set; }
    [JsonPropertyName("topHeight")] public double? TopHeight { get; set; }
}

/// <summary>The shapes of one project, split by type.</summary>
public sealed class InnerSpaceShapes
{
    public List<InnerSpaceMapShape> Maps { get; } = new();
    public List<InnerSpaceScaleShape> Scales { get; } = new();
    public List<InnerSpaceWallShape> Walls { get; } = new();
    public List<InnerSpaceDeviceShape> Devices { get; } = new();

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Splits the project's shape list by <c>type</c>; unknown types are skipped.</summary>
    public static InnerSpaceShapes From(IEnumerable<JsonElement> shapes)
    {
        var result = new InnerSpaceShapes();
        foreach (var s in shapes)
        {
            if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("type", out var t)) continue;
            switch (t.GetString())
            {
                case "map": Add(result.Maps, s); break;
                case "scale": Add(result.Scales, s); break;
                case "wall": Add(result.Walls, s); break;
                case "device": Add(result.Devices, s); break;
            }
        }
        return result;
    }

    private static void Add<T>(List<T> list, JsonElement e)
    {
        try
        {
            var v = e.Deserialize<T>(Opts);
            if (v != null) list.Add(v);
        }
        catch (JsonException)
        {
            // One malformed shape must not fail the whole import.
        }
    }
}

/// <summary>Apple RoomPlan <c>CapturedRoom</c> (draft/draft.json inside a scan's zip): only what we read.</summary>
[VendorSpecific("Apple", "RoomPlan CapturedRoom JSON, version 2")]
public sealed class RoomPlanCapture
{
    [JsonPropertyName("walls")] public List<RoomPlanSurface> Walls { get; set; } = new();
    [JsonPropertyName("windows")] public List<RoomPlanSurface> Windows { get; set; } = new();
    [JsonPropertyName("doors")] public List<RoomPlanSurface> Doors { get; set; } = new();
    [JsonPropertyName("floors")] public List<RoomPlanSurface> Floors { get; set; } = new();
    [JsonPropertyName("story")] public int? Story { get; set; }
}

/// <summary>A RoomPlan wall, window, door or floor: size in meters and a column-major 4x4 pose.</summary>
public sealed class RoomPlanSurface
{
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("parentIdentifier")] public string? ParentIdentifier { get; set; }
    [JsonPropertyName("dimensions")] public List<double> Dimensions { get; set; } = new();
    [JsonPropertyName("transform")] public List<double> Transform { get; set; } = new();
}
