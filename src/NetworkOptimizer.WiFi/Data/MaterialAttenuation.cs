namespace NetworkOptimizer.WiFi.Data;

/// <summary>
/// Built-in material attenuation constants for RF propagation modeling.
/// Values are signal loss in dB when passing through the material.
/// </summary>
public static class MaterialAttenuation
{
    public record AttenuationValues(double Ghz2_4, double Ghz5, double Ghz6);

    /// <summary>Material type to attenuation mapping per frequency band</summary>
    public static readonly Dictionary<string, AttenuationValues> Materials = new(StringComparer.OrdinalIgnoreCase)
    {
        ["drywall"]         = new(3,  4,  5),
        ["wood"]            = new(4,  5,  6),
        ["glass"]           = new(3,  4,  5),
        ["brick"]           = new(7,  11, 14),
        ["concrete"]        = new(12, 17, 22),
        ["exterior"]        = new(15, 20, 25),
        ["metal"]           = new(25, 28, 30),
        ["floor_wood"]      = new(13, 18, 22),
        ["floor_concrete"]  = new(18, 23, 28),
    };

    /// <summary>Display colors for each material type (CSS hex)</summary>
    public static readonly Dictionary<string, string> MaterialColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["drywall"]         = "#94a3b8",
        ["wood"]            = "#a78bfa",
        ["glass"]           = "#67e8f9",
        ["brick"]           = "#fb923c",
        ["concrete"]        = "#f87171",
        ["exterior"]        = "#ef4444",
        ["metal"]           = "#fbbf24",
        ["floor_wood"]      = "#8b5cf6",
        ["floor_concrete"]  = "#dc2626",
    };

    /// <summary>Get attenuation for a material at a specific frequency band</summary>
    public static double GetAttenuation(string material, string band)
    {
        if (!Materials.TryGetValue(material, out var values))
            return 5.0; // default fallback

        return band switch
        {
            "2.4" or "2.4GHz" or "2.4 GHz" => values.Ghz2_4,
            "5" or "5GHz" or "5 GHz"       => values.Ghz5,
            "6" or "6GHz" or "6 GHz"       => values.Ghz6,
            _ => values.Ghz5 // default to 5 GHz
        };
    }

    /// <summary>Get center frequency in MHz for a band</summary>
    public static double GetCenterFrequencyMhz(string band)
    {
        return band switch
        {
            "2.4" or "2.4GHz" or "2.4 GHz" => 2437.0,
            "5" or "5GHz" or "5 GHz"       => 5500.0,
            "6" or "6GHz" or "6 GHz"       => 6500.0,
            _ => 5500.0
        };
    }
}
