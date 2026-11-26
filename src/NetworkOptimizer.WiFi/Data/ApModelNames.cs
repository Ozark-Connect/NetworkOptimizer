namespace NetworkOptimizer.WiFi.Data;

/// <summary>
/// Normalizes AP product names for lookups keyed by the base model (antenna patterns,
/// radio defaults, mount type).
/// </summary>
public static class ApModelNames
{
    /// <summary>
    /// Strip a black color suffix: "-B" (U7-Pro-XG-B) or "-BK" (UWB-XG-BK). Ubiquiti's
    /// catalog gives each the same product name and radios as its base model.
    /// </summary>
    /// <param name="model">Product name, e.g. "U7-Pro-XG-B".</param>
    /// <returns>The base model name, or the input unchanged.</returns>
    public static string StripColorSuffix(string model)
    {
        if (model.EndsWith("-BK", StringComparison.OrdinalIgnoreCase))
            return model[..^3];
        if (model.EndsWith("-B", StringComparison.OrdinalIgnoreCase))
            return model[..^2];
        return model;
    }
}
