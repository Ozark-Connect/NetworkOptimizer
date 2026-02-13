namespace NetworkOptimizer.WiFi.Data;

/// <summary>
/// Static catalog of AP model capabilities for planned AP placement.
/// Provides TX power defaults, antenna gain, and band support when no live API data is available.
/// </summary>
public static class ApModelCatalog
{
    /// <summary>
    /// Per-band radio defaults for a planned AP model.
    /// </summary>
    public class BandDefaults
    {
        public int DefaultTxPowerDbm { get; init; }
        public int MinTxPowerDbm { get; init; }
        public int MaxTxPowerDbm { get; init; }
        public int AntennaGainDbi { get; init; }
    }

    /// <summary>
    /// Catalog entry for an AP model.
    /// </summary>
    public class ApModelInfo
    {
        public required string Model { get; init; }
        public required Dictionary<string, BandDefaults> Bands { get; init; }
        public required string DefaultMountType { get; init; }
        public bool HasOmniVariant { get; init; }
    }

    // Fallback defaults for models not in the hardcoded table
    private static readonly BandDefaults Default24 = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 3 };
    private static readonly BandDefaults Default5 = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 5 };
    private static readonly BandDefaults Default6 = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 5 };

    /// <summary>
    /// Hardcoded per-model TX power ranges and antenna gains.
    /// Values sourced from UniFi datasheets. Models not listed here use fallback defaults.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, BandDefaults>> ModelDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        // === Wi-Fi 7 APs ===
        ["U7-Pro"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 6 },
            ["6"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 6 },
        },
        ["U7-Pro-Max"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 25, MinTxPowerDbm = 1, MaxTxPowerDbm = 28, AntennaGainDbi = 6 },
            ["6"] = new() { DefaultTxPowerDbm = 25, MinTxPowerDbm = 1, MaxTxPowerDbm = 28, AntennaGainDbi = 6 },
        },
        ["U7-Pro-Wall"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 5 },
            ["6"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 5 },
        },
        ["U7-Pro-XG"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 25, MinTxPowerDbm = 1, MaxTxPowerDbm = 28, AntennaGainDbi = 6 },
            ["6"] = new() { DefaultTxPowerDbm = 25, MinTxPowerDbm = 1, MaxTxPowerDbm = 28, AntennaGainDbi = 6 },
        },
        ["U7-Pro-XG-Wall"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 25, MinTxPowerDbm = 1, MaxTxPowerDbm = 28, AntennaGainDbi = 6 },
            ["6"] = new() { DefaultTxPowerDbm = 25, MinTxPowerDbm = 1, MaxTxPowerDbm = 28, AntennaGainDbi = 6 },
        },
        ["U7-Pro-XGS"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 25, MinTxPowerDbm = 1, MaxTxPowerDbm = 28, AntennaGainDbi = 6 },
            ["6"] = new() { DefaultTxPowerDbm = 25, MinTxPowerDbm = 1, MaxTxPowerDbm = 28, AntennaGainDbi = 6 },
        },
        ["U7-Lite"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 3 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 25, AntennaGainDbi = 5 },
        },
        ["U7-LR"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 5 },
        },
        ["U7-IW"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 22, AntennaGainDbi = 2 },
            ["5"] = new() { DefaultTxPowerDbm = 22, MinTxPowerDbm = 1, MaxTxPowerDbm = 24, AntennaGainDbi = 4 },
        },
        ["U7-Outdoor"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 5 },
        },
        ["U7-Pro-Outdoor"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 6 },
            ["6"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 6 },
        },

        // === Wi-Fi 6E APs ===
        ["E7"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 6 },
            ["6"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 6 },
        },
        ["E7-Campus"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 6 },
            ["6"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 6 },
        },
        ["U6-Enterprise"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 25, AntennaGainDbi = 6 },
            ["6"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 25, AntennaGainDbi = 6 },
        },
        ["U6-Enterprise-IW"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 22, AntennaGainDbi = 3 },
            ["5"] = new() { DefaultTxPowerDbm = 22, MinTxPowerDbm = 1, MaxTxPowerDbm = 24, AntennaGainDbi = 4 },
            ["6"] = new() { DefaultTxPowerDbm = 22, MinTxPowerDbm = 1, MaxTxPowerDbm = 24, AntennaGainDbi = 4 },
        },

        // === Wi-Fi 6 APs ===
        ["U6-Pro"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 22, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 25, AntennaGainDbi = 6 },
        },
        ["U6-LR"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 25, AntennaGainDbi = 5 },
        },
        ["U6-Lite"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 22, AntennaGainDbi = 3 },
            ["5"] = new() { DefaultTxPowerDbm = 22, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
        },
        ["U6-Plus"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 22, AntennaGainDbi = 3 },
            ["5"] = new() { DefaultTxPowerDbm = 22, MinTxPowerDbm = 1, MaxTxPowerDbm = 25, AntennaGainDbi = 5 },
        },
        ["U6-Mesh"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 22, AntennaGainDbi = 3 },
            ["5"] = new() { DefaultTxPowerDbm = 22, MinTxPowerDbm = 1, MaxTxPowerDbm = 25, AntennaGainDbi = 5 },
        },
        ["U6-Mesh-Pro"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 22, AntennaGainDbi = 4 },
            ["5"] = new() { DefaultTxPowerDbm = 23, MinTxPowerDbm = 1, MaxTxPowerDbm = 26, AntennaGainDbi = 8 },
        },
        ["U6-IW"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 22, AntennaGainDbi = 2 },
            ["5"] = new() { DefaultTxPowerDbm = 22, MinTxPowerDbm = 1, MaxTxPowerDbm = 24, AntennaGainDbi = 4 },
        },
        ["U6-Extender"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 20, AntennaGainDbi = 2 },
            ["5"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 22, AntennaGainDbi = 3 },
        },

        // === Gateways with Wi-Fi ===
        ["UDM"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 18, MinTxPowerDbm = 1, MaxTxPowerDbm = 20, AntennaGainDbi = 3 },
            ["5"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
        },
        ["UDR"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 18, MinTxPowerDbm = 1, MaxTxPowerDbm = 20, AntennaGainDbi = 2 },
            ["5"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 3 },
        },
        ["UDR7"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 18, MinTxPowerDbm = 1, MaxTxPowerDbm = 20, AntennaGainDbi = 3 },
            ["5"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
        },
        ["UDW"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 18, MinTxPowerDbm = 1, MaxTxPowerDbm = 20, AntennaGainDbi = 2 },
            ["5"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 22, AntennaGainDbi = 3 },
        },
        ["UX"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 18, MinTxPowerDbm = 1, MaxTxPowerDbm = 20, AntennaGainDbi = 2 },
            ["5"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 22, AntennaGainDbi = 3 },
        },
        ["UX7"] = new()
        {
            ["2.4"] = new() { DefaultTxPowerDbm = 18, MinTxPowerDbm = 1, MaxTxPowerDbm = 20, AntennaGainDbi = 3 },
            ["5"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
            ["6"] = new() { DefaultTxPowerDbm = 20, MinTxPowerDbm = 1, MaxTxPowerDbm = 23, AntennaGainDbi = 4 },
        },
    };

    /// <summary>
    /// Build the full catalog by combining antenna pattern data with hardcoded defaults.
    /// </summary>
    public static List<ApModelInfo> BuildCatalog(AntennaPatternLoader patternLoader)
    {
        var models = patternLoader.GetAllBaseModelNames();
        var catalog = new List<ApModelInfo>();

        foreach (var model in models)
        {
            var supportedBands = patternLoader.GetSupportedBands(model);
            if (supportedBands.Count == 0) continue;

            var bands = new Dictionary<string, BandDefaults>();
            foreach (var band in supportedBands)
            {
                if (ModelDefaults.TryGetValue(model, out var modelBands) &&
                    modelBands.TryGetValue(band, out var specific))
                {
                    bands[band] = specific;
                }
                else
                {
                    // Use fallback defaults
                    bands[band] = band switch
                    {
                        "2.4" => Default24,
                        "6" => Default6,
                        _ => Default5
                    };
                }
            }

            catalog.Add(new ApModelInfo
            {
                Model = model,
                Bands = bands,
                DefaultMountType = MountTypeHelper.GetDefaultMountType(model),
                HasOmniVariant = patternLoader.HasOmniVariant(model),
            });
        }

        return catalog;
    }

    /// <summary>
    /// Get band defaults for a specific model and band.
    /// Returns fallback defaults if the model is not in the hardcoded table.
    /// </summary>
    public static BandDefaults GetBandDefaults(string model, string band)
    {
        if (ModelDefaults.TryGetValue(model, out var modelBands) &&
            modelBands.TryGetValue(band, out var specific))
        {
            return specific;
        }

        return band switch
        {
            "2.4" => Default24,
            "6" => Default6,
            _ => Default5
        };
    }
}
