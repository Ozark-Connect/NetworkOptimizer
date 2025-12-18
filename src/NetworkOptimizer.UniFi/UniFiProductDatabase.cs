namespace NetworkOptimizer.UniFi;

/// <summary>
/// Maps UniFi internal model codes to friendly product names.
/// The UniFi API returns internal codes (model/shortname), but the UI displays
/// friendly names. This database provides the translation.
///
/// Sources:
/// - https://ubntwiki.com/products/software/unifi-controller/api
/// - UniFi device discovery and community documentation
/// </summary>
public static class UniFiProductDatabase
{
    /// <summary>
    /// MIPS architecture devices that cannot run iperf3.
    /// These devices use MIPS processors with incompatible binary loaders.
    /// </summary>
    private static readonly HashSet<string> MipsDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "USW-Flex-Mini",
        "USW-Pro-XG-8-PoE",
        "USW-Lite-8-PoE"
    };

    /// <summary>
    /// Map of model code to friendly product name
    /// </summary>
    private static readonly Dictionary<string, string> ModelToProductName = new(StringComparer.OrdinalIgnoreCase)
    {
        // ===== Gateways / Security Gateways =====
        // UniFi Dream Machine family
        { "UDMPRO", "UDM-Pro" },
        { "UDMPROSE", "UDM-Pro-SE" },
        { "UDMPROMAX", "UDM-Pro-Max" },
        { "UDM", "UDM" },
        { "UDMSE", "UDM-SE" },
        { "UDMA6A8", "UCG-Fiber" },     // UCG-Fiber internal model
        { "UCGF", "UCG-Fiber" },         // UCG-Fiber shortname
        { "UCG", "UCG" },
        { "UCKP", "UCK-Plus" },
        { "UCK", "Cloud Key" },
        { "UCKG2", "Cloud Key Gen2" },
        { "UCKP2", "Cloud Key Gen2 Plus" },

        // UniFi Security Gateways
        { "UGW3", "USG-3P" },
        { "UGW4", "USG-Pro-4" },
        { "UGWXG", "USG-XG-8" },
        { "USG", "USG" },

        // UniFi Express
        { "UXG", "UXG" },
        { "UXGPRO", "UXG-Pro" },
        { "UXGLITE", "UXG-Lite" },

        // ===== Switches =====
        // USW Lite Series
        { "USWLITE8", "USW-Lite-8-PoE" },
        { "USWLITE16", "USW-Lite-16-PoE" },
        { "USL8LP", "USW-Lite-8-PoE" },
        { "USL8LPB", "USW-Lite-8-PoE" },
        { "USL16LP", "USW-Lite-16-PoE" },
        { "USL16LPB", "USW-Lite-16-PoE" },

        // USW Flex Series
        { "USWFLEX", "USW-Flex" },
        { "USWFLEXMINI", "USW-Flex-Mini" },
        { "USFXG", "USW-Flex-XG" },
        { "USMINI", "USW-Flex-Mini" },

        // USW Standard Series
        { "USW8", "USW-8" },
        { "USW8P60", "USW-8-60W" },
        { "USW8P150", "USW-8-150W" },
        { "USW16P150", "USW-16-PoE" },
        { "USW24", "USW-24" },
        { "USW24P250", "USW-24-PoE" },
        { "USW24P500", "USW-24-PoE-500W" },
        { "USW48", "USW-48" },
        { "USW48P500", "USW-48-PoE" },
        { "USW48P750", "USW-48-PoE-750W" },

        // USW Pro Series
        { "USWPRO24", "USW-Pro-24" },
        { "USWPRO24POE", "USW-Pro-24-PoE" },
        { "USWPRO48", "USW-Pro-48" },
        { "USWPRO48POE", "USW-Pro-48-PoE" },
        { "US24P250", "USW-Pro-24-PoE" },
        { "US24P500", "USW-Pro-24-PoE-500W" },
        { "US48P500", "USW-Pro-48-PoE" },
        { "US48P750", "USW-Pro-48-PoE-750W" },

        // USW Pro XG Series (shortname format: US=USW, P=Pro, XG=XG, 8=8, P=PoE)
        { "USPXG8P", "USW-Pro-XG-8-PoE" },         // Your switch
        { "USWED76", "USW-Pro-XG-8-PoE" },         // Model code for same device

        // USW Enterprise Series
        { "USWENTERPRISE8POE", "USW-Enterprise-8-PoE" },
        { "USWENTERPRISE24POE", "USW-Enterprise-24-PoE" },
        { "USWENTERPRISE48POE", "USW-Enterprise-48-PoE" },
        { "USWENTERPRISEXG24", "USW-EnterpriseXG-24" },

        // USW Aggregation
        { "USWAGGREGATION", "USW-Aggregation" },
        { "USWAGGPRO", "USW-Aggregation-Pro" },
        { "US16XG", "USW-16-XG" },

        // US (older generation) Switches
        { "US8", "US-8" },
        { "US8P60", "US-8-60W" },
        { "US8P150", "US-8-150W" },
        { "US16P150", "US-16-150W" },
        { "US24", "US-24" },
        { "US48", "US-48" },
        // Note: US24P250, US24P500, US48P500, US48P750 defined in Pro series above

        // ===== Access Points =====
        // WiFi 7 (U7) Series
        { "U7PRO", "U7-Pro" },
        { "U7PROMAX", "U7-Pro-Max" },
        { "U7PROMAXB", "U7-Pro-Max" },
        { "U7PROXGSB", "U7-Pro-XGS-B" }, // Your Tiny Home AP
        { "U7PIW", "U7-Pro-Wall" },           // Pro In-Wall
        { "U7PO", "U7-Pro-Outdoor" },         // Pro Outdoor
        { "U7LR", "U7-LR" },
        { "U7LITE", "U7-Lite" },

        // WiFi 6E (U6E) Series
        { "U6ENTERPRISEB", "U6-Enterprise" },
        { "U6ENTERPRISEINWALL", "U6-Enterprise-IW" },
        { "U6MESH", "U6-Mesh" },

        // WiFi 6 (U6) Series
        { "U6PRO", "U6-Pro" },
        { "U6LR", "U6-LR" },
        { "U6LITE", "U6-Lite" },
        { "U6PLUS", "U6+" },
        { "U6EXTENDER", "U6-Extender" },
        { "U6IW", "U6-IW" },
        // Note: U6MESH defined in U6E series above

        // AC Series (older)
        { "UAPPRO", "UAP-AC-Pro" },
        { "UAPLR", "UAP-AC-LR" },
        { "UAPLITE", "UAP-AC-Lite" },
        { "UAPM", "UAP-AC-M" },
        { "UAPMESH", "UAP-AC-Mesh" },
        { "UAPMESHPRO", "UAP-AC-Mesh-Pro" },
        { "UAPIW", "UAP-IW" },
        { "UAPIWPRO", "UAP-IW-Pro" },
        { "UAPNANOHD", "UAP-nanoHD" },
        { "UAPHD", "UAP-HD" },
        { "UAPSHD", "UAP-SHD" },
        { "UAPXG", "UAP-XG" },
        { "UAPBASESTATION", "UAP-BaseStationXG" },
        { "UFLEXHD", "UAP-FlexHD" },

        // U7 Outdoor variants
        { "UKPW", "U7-Outdoor" },            // U7 Outdoor (Front Yard AP)

        // ===== Other Devices =====
        // UniFi Protect
        { "UNVR", "UNVR" },
        { "UNVRPRO", "UNVR-Pro" },

        // UniFi Connect
        { "ULED", "UC-LED" },

        // ===== Cellular Modems =====
        { "U5GMAX", "U5G-Max" },
        { "ULTE", "U-LTE" },
        { "ULTEPRO", "U-LTE-Pro" },
    };

    /// <summary>
    /// Get the friendly product name for a model code
    /// </summary>
    /// <param name="modelCode">The model or shortname from the UniFi API</param>
    /// <returns>Friendly product name, or the original code if not found</returns>
    public static string GetProductName(string? modelCode)
    {
        if (string.IsNullOrEmpty(modelCode))
            return "Unknown";

        // Try direct lookup
        if (ModelToProductName.TryGetValue(modelCode, out var name))
            return name;

        // Return original if not found - this helps identify new models
        return modelCode;
    }

    /// <summary>
    /// Get the best available product name from multiple fields
    /// </summary>
    /// <param name="model">The model field (internal code)</param>
    /// <param name="shortname">The shortname field</param>
    /// <param name="modelDisplay">The model_display field (if present)</param>
    /// <returns>Best available friendly name</returns>
    public static string GetBestProductName(string? model, string? shortname, string? modelDisplay)
    {
        // Try model_display first if it looks like a product name
        if (!string.IsNullOrEmpty(modelDisplay) && modelDisplay.Contains("-"))
            return modelDisplay;

        // Try shortname lookup
        var shortnameLookup = GetProductName(shortname);
        if (!string.IsNullOrEmpty(shortname) && shortnameLookup != shortname)
            return shortnameLookup;

        // Try model lookup
        var modelLookup = GetProductName(model);
        if (!string.IsNullOrEmpty(model) && modelLookup != model)
            return modelLookup;

        // Fall back to shortname, then model
        return shortname ?? model ?? "Unknown";
    }

    /// <summary>
    /// Check if a device uses MIPS architecture (cannot run iperf3)
    /// </summary>
    /// <param name="productName">The friendly product name (e.g., "USW-Flex-Mini")</param>
    /// <returns>True if the device is MIPS-based</returns>
    public static bool IsMipsArchitecture(string? productName)
    {
        if (string.IsNullOrEmpty(productName))
            return false;

        return MipsDevices.Contains(productName);
    }

    /// <summary>
    /// Check if a device uses MIPS architecture using multiple identification fields
    /// </summary>
    /// <param name="model">The model field (internal code)</param>
    /// <param name="shortname">The shortname field</param>
    /// <param name="modelDisplay">The model_display field (if present)</param>
    /// <returns>True if the device is MIPS-based</returns>
    public static bool IsMipsArchitecture(string? model, string? shortname, string? modelDisplay)
    {
        var productName = GetBestProductName(model, shortname, modelDisplay);
        return IsMipsArchitecture(productName);
    }
}
