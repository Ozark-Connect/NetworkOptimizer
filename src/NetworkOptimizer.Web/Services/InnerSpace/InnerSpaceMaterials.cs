using System.Text;
using NetworkOptimizer.WiFi.Data;

namespace NetworkOptimizer.Web.Services.InnerSpace;

/// <summary>
/// Maps InnerSpace wall materials to ours. Resolution follows InnerSpace: a wall's custom type, else
/// an override of its built-in material, else the built-in. The result maps by key, then by
/// normalized name, then by the closest 5 GHz loss.
/// </summary>
public sealed class InnerSpaceMaterials
{
    /// <summary>InnerSpace's built-in heights for openings, in meters (swai.js door / window presets).</summary>
    public static readonly WallSpan DoorSpan = new(0, 2.0);
    public static readonly WallSpan WindowSpan = new(0.75, 2.0);

    // Materials a wall can map to by nearest loss: not doors, windows, floors or the legacy alias.
    private static readonly string[] WallCandidates = MaterialAttenuation.Materials.Keys
        .Where(k => !k.StartsWith("door_") && !k.StartsWith("window_") && !k.StartsWith("floor_") && k != "exterior")
        .ToArray();

    private readonly Dictionary<string, InnerSpaceWallType> _typesById;
    private readonly Dictionary<string, InnerSpaceWallType> _overrides;
    private static readonly Dictionary<string, string> KeysByNormalizedName = BuildNameIndex();

    public InnerSpaceMaterials(IEnumerable<InnerSpaceWallType> wallTypes)
    {
        var live = wallTypes.Where(t => !t.IsDeleted).ToList();
        _typesById = live.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        _overrides = live.Where(t => !string.IsNullOrEmpty(t.OverridesVariant))
            .GroupBy(t => t.OverridesVariant!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The result of resolving one InnerSpace wall.</summary>
    public sealed record Resolved(string Material, WallSpan? Span, bool IsScanDefault);

    /// <summary>
    /// Resolves one wall. <paramref name="isScannedPlan"/> marks a LiDAR plan, where Concrete with no
    /// custom type and no Concrete override is the scan's default, not the author's choice.
    /// </summary>
    public Resolved Resolve(string variant, string? wallTypeId, bool isScannedPlan)
    {
        variant = string.IsNullOrEmpty(variant) ? "concrete" : variant;

        if (!string.IsNullOrEmpty(wallTypeId) && _typesById.TryGetValue(wallTypeId, out var custom))
            return FromType(custom);

        if (_overrides.TryGetValue(variant, out var over))
        {
            var byName = ByName(over.Name);
            if (byName != null)
                return new Resolved(byName, SpanOf(over) ?? DefaultSpan(byName), false);
            // An override whose name maps to nothing keeps the built-in, as TJ decided.
        }

        if (isScannedPlan && variant.Equals("concrete", StringComparison.OrdinalIgnoreCase))
            return new Resolved("concrete", null, true);

        var key = MaterialAttenuation.Materials.ContainsKey(variant) ? variant.ToLowerInvariant() : "concrete";
        return new Resolved(key, DefaultSpan(key), false);
    }

    private static Resolved FromType(InnerSpaceWallType type)
    {
        var key = ByName(type.Name) ?? Nearest(type.Attenuation);
        return new Resolved(key, SpanOf(type) ?? DefaultSpan(key), false);
    }

    private static WallSpan? SpanOf(InnerSpaceWallType t) =>
        t.BottomHeight == null && t.TopHeight == null ? null : new WallSpan(t.BottomHeight, t.TopHeight);

    /// <summary>InnerSpace's default height band for a material: doors and windows have one, walls do not.</summary>
    public static WallSpan? DefaultSpan(string key) =>
        key.StartsWith("door_", StringComparison.OrdinalIgnoreCase) ? DoorSpan
        : key.StartsWith("window_", StringComparison.OrdinalIgnoreCase) ? WindowSpan
        : null;

    /// <summary>True for a door or window material: an opening cut into its host wall.</summary>
    public static bool IsOpening(string key) =>
        key.StartsWith("door_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("window_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Our material key whose key or label matches <paramref name="name"/>, ignoring case and punctuation.</summary>
    public static string? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return KeysByNormalizedName.TryGetValue(Normalize(name), out var key) ? key : null;
    }

    /// <summary>The wall material whose 5 GHz loss is closest to <paramref name="db"/>.</summary>
    public static string Nearest(double db) =>
        WallCandidates.OrderBy(k => Math.Abs(MaterialAttenuation.Materials[k].Ghz5 - db)).ThenBy(k => k).First();

    /// <summary>Our floor material closest to an InnerSpace slab loss.</summary>
    public static string NearestFloor(double db) =>
        new[] { "floor_wood", "floor_concrete" }
            .OrderBy(k => Math.Abs(MaterialAttenuation.Materials[k].Ghz5 - db)).First();

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static Dictionary<string, string> BuildNameIndex()
    {
        var index = new Dictionary<string, string>();
        foreach (var (key, label) in MaterialAttenuation.MaterialLabels)
        {
            if (key == "exterior") continue; // legacy alias; "Exterior Wall" stays unmapped
            index.TryAdd(Normalize(key), key);
            index.TryAdd(Normalize(label), key);
        }
        return index;
    }
}
