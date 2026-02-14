namespace NetworkOptimizer.WiFi.Data;

/// <summary>
/// Resolves AP mount type (ceiling, wall, desktop) from saved value or model name.
/// </summary>
public static class MountTypeHelper
{
    private static readonly HashSet<string> WallModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "UAP-BeaconHD", "UDW", "UDB-Pro", "UDB-Pro-Sector", "UMA-D", "U6-Extender",
        "E7-Audience", "E7-Audience-EU", "E7-Campus", "E7-Campus-EU"
    };

    private static readonly HashSet<string> DesktopModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "UDM", "UDR", "UDR7", "UX", "UX7"
    };

    /// <summary>
    /// Infer default mount type from AP model name.
    /// </summary>
    public static string GetDefaultMountType(string model)
    {
        if (string.IsNullOrEmpty(model))
            return "ceiling";

        // Strip color suffix (e.g., "-B" for black) before checking
        var m = model.EndsWith("-B", StringComparison.OrdinalIgnoreCase)
            ? model[..^2]
            : model;

        if (WallModels.Contains(m))
            return "wall";

        if (DesktopModels.Contains(m))
            return "desktop";

        // Check for wall-mount indicators in model name
        if (m.Contains("-IW", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("-Wall", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("-Outdoor", StringComparison.OrdinalIgnoreCase))
            return "wall";

        return "ceiling";
    }

    /// <summary>
    /// Return saved mount type if set, otherwise infer from model name.
    /// </summary>
    public static string Resolve(string? savedMountType, string model)
    {
        return !string.IsNullOrEmpty(savedMountType) ? savedMountType : GetDefaultMountType(model);
    }
}
