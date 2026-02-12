namespace NetworkOptimizer.WiFi.Data;

/// <summary>
/// Built-in material attenuation constants for RF propagation modeling.
/// Values are signal loss in dB when passing through the material.
/// Per-band values (2.4/5/6 GHz) scaled from UniFi Design Center reference values.
/// </summary>
public static class MaterialAttenuation
{
    public record AttenuationValues(double Ghz2_4, double Ghz5, double Ghz6);

    /// <summary>Display names for material types</summary>
    public static readonly Dictionary<string, string> MaterialLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["drywall"]         = "Drywall (Standard)",
        ["drywall_heavy"]   = "Drywall (Heavy Duty)",
        ["wood"]            = "Wood",
        ["wood_paneling"]   = "Wood Paneling",
        ["glass"]           = "Glass (Standard)",
        ["glass_thin"]      = "Glass (Thin)",
        ["brick"]           = "Brick",
        ["concrete"]        = "Concrete",
        ["metal"]           = "Metal",
        ["door_wood"]       = "Door (Wood)",
        ["door_metal"]      = "Door (Metal)",
        ["door_glass"]      = "Door (Glass)",
        ["window_1_pane"]   = "Window (Single Pane)",
        ["window_2_pane"]   = "Window (Double Pane)",
        ["window_3_pane"]   = "Window (Triple Pane)",
        ["exterior"]                = "Exterior Wall",
        ["exterior_residential"]    = "Exterior (Residential)",
        ["exterior_commercial"]     = "Exterior (Commercial)",
        ["floor_wood"]              = "Floor (Wood Frame)",
        ["floor_concrete"]          = "Floor (Concrete Slab)",
    };

    /// <summary>Material type to attenuation mapping per frequency band</summary>
    public static readonly Dictionary<string, AttenuationValues> Materials = new(StringComparer.OrdinalIgnoreCase)
    {
        // Walls - aligned with UniFi Design Center reference values
        ["drywall"]         = new(2,   3,   4),
        ["drywall_heavy"]   = new(3,   4,   5),
        ["wood"]            = new(4,   5,   6),
        ["wood_paneling"]   = new(1,   2,   3),
        ["glass"]           = new(1,   2,   3),
        ["glass_thin"]      = new(1,   1,   2),
        ["brick"]           = new(4,   5,   7),
        ["concrete"]        = new(12,  15,  18),
        ["metal"]           = new(8,   10,  12),
        // Doors
        ["door_wood"]       = new(4,   5,   6),
        ["door_metal"]      = new(8,   10,  12),
        ["door_glass"]      = new(1,   2,   3),
        // Windows
        ["window_1_pane"]   = new(3,   4,   5),
        ["window_2_pane"]   = new(5,   7,   9),
        ["window_3_pane"]   = new(8,   10,  12),
        // Exterior walls
        ["exterior"]                = new(5,   7,   8),   // backward compat alias → residential
        ["exterior_residential"]    = new(5,   7,   8),   // wood frame + insulation + siding (NIST: 3-8 dB at 2.4 GHz)
        ["exterior_commercial"]     = new(10,  15,  18),  // brick/masonry + block (NIST: ~10 dB at 2.4 GHz)
        // Floors (ITU-R P.1238)
        ["floor_wood"]      = new(5,   8,   10),  // residential wood frame
        ["floor_concrete"]  = new(15,  18,  21),  // commercial concrete slab
    };

    /// <summary>Display colors for each material type (CSS hex, aligned with UniFi Design Center)</summary>
    public static readonly Dictionary<string, string> MaterialColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["drywall"]         = "#8bc1d1",  // rgb(139, 193, 209)
        ["drywall_heavy"]   = "#40859a",  // rgb(64, 133, 154)
        ["wood"]            = "#f5a623",  // rgb(245, 166, 35)
        ["wood_paneling"]   = "#d4a76a",  // warm wood tone
        ["glass"]           = "#66a9ff",  // rgb(102, 169, 255)
        ["glass_thin"]      = "#c1d8f7",  // rgb(193, 216, 247)
        ["brick"]           = "#9c2628",  // rgb(156, 38, 40)
        ["concrete"]        = "#517197",  // rgb(81, 113, 151)
        ["metal"]           = "#959595",  // rgb(149, 149, 149)
        ["door_wood"]       = "#c4851c",  // rgb(196, 133, 28)
        ["door_metal"]      = "#818181",  // rgb(129, 129, 129)
        ["door_glass"]      = "#b1c3da",  // rgb(177, 195, 218)
        ["window_1_pane"]   = "#8ebbf4",  // rgb(142, 187, 244)
        ["window_2_pane"]   = "#2082ff",  // rgb(32, 130, 255)
        ["window_3_pane"]   = "#025bcc",  // rgb(2, 91, 204)
        ["exterior"]                = "#ef4444",
        ["exterior_residential"]    = "#f97316",  // orange - lighter than commercial
        ["exterior_commercial"]     = "#dc2626",  // red - heavy material
        ["floor_wood"]              = "#8b5cf6",
        ["floor_concrete"]          = "#dc2626",
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
