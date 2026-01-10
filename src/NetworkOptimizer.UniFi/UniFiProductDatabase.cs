namespace NetworkOptimizer.UniFi;

/// <summary>
/// Maps UniFi internal model codes to friendly product names.
/// The UniFi API returns internal codes (model/shortname), but the UI displays
/// friendly names. This database provides the translation.
///
/// Sources:
/// - https://ubntwiki.com/products/software/unifi-controller/api
/// - UniFi device discovery and community documentation
/// - UniFi firmware release groups
/// </summary>
public static class UniFiProductDatabase
{
    /// <summary>
    /// Devices that cannot run iperf3.
    /// Note: Not all of these are MIPS architecture, but they are all known to not include iperf3.
    /// </summary>
    private static readonly HashSet<string> MipsDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        // Flex Series (all non-rackmount switches are MIPS)
        "USW-Flex",
        "USW-Flex-Mini",
        "USW-Flex-XG",
        "USW-Flex-2.5G-5",
        "USW-Flex-2.5G-8",
        "USW-Flex-2.5G-8-PoE",

        // Ultra Series
        "USW-Ultra",
        "USW-Ultra-60W",
        "USW-Ultra-210W",

        // Lite Series (non-rackmount)
        "USW-Lite-8-PoE",
        "USW-Lite-16-PoE",

        // Industrial
        "USW-Industrial",

        // Pro XG (MIPS-based)
        "USW-Pro-XG-8-PoE",

        // Legacy US Series (MIPS-based)
        "US-8",
        "US-8-60W",
        "US-8-150W",

        // Enterprise/Aggregation switches (no iperf3)
        "USW-24-PoE",
        "USW-Enterprise-8-PoE",
        "USW-Aggregation",

        // AC APs (no iperf3)
        "UAP",
        "UAP-LR",
        "UAP-IW",
        "UAP-Outdoor",
        "UAP-Outdoor+",
        "UAP-Outdoor5",
        "UAP-AC-Pro",
        "UAP-AC-Lite",
    };

    /// <summary>
    /// Map of model code to friendly product name
    /// </summary>
    private static readonly Dictionary<string, string> ModelToProductName = new(StringComparer.OrdinalIgnoreCase)
    {
        // =====================================================================
        // GATEWAYS / SECURITY GATEWAYS
        // =====================================================================

        // UniFi Dream Machine family
        { "UDMPRO", "UDM-Pro" },
        { "UDM-PRO", "UDM-Pro" },
        { "UDMPROSE", "UDM-SE" },
        { "UDM-PRO-SE", "UDM-Pro-SE" },
        { "UDMPROMAX", "UDM-Pro-Max" },
        { "UDM-PRO-MAX", "UDM-Pro-Max" },
        { "UDM", "UDM" },
        { "UDMSE", "UDM-SE" },

        // Dream Wall
        { "UDW", "UDW" },

        // Enterprise Fortress Gateway
        { "EFG", "EFG" },
        { "UDMENT", "EFG" },

        // Cloud Gateways
        { "UCG", "UCG" },
        { "UCGF", "UCG-Fiber" },
        { "UDMA6A8", "UCG-Fiber" },
        { "UCGMAX", "UCG-Max" },
        { "UCG-ULTRA", "UCG-Ultra" },

        // Cloud Keys
        { "UCK", "UC-CK" },
        { "UCK-G2", "UCK-G2" },
        { "UCKG2", "UCK-G2" },
        { "UCKP", "UCK-G2-Plus" },
        { "UCK-G2-PLUS", "UCK-G2-Plus" },
        { "UCKP2", "UCK-G2-Plus" },
        { "UCKENT", "CK-Enterprise" },

        // UniFi Security Gateways (legacy)
        { "USG", "USG" },
        { "UGW", "USG" },
        { "UGW3", "USG-3P" },
        { "UGW4", "USG-Pro-4" },
        { "UGWXG", "USG-XG-8" },

        // UniFi Gateways (Next-Gen)
        { "UXG", "UXG" },
        { "UXGPRO", "UXG-Pro" },
        { "UXG-PRO", "UXG-Pro" },
        { "UXGPROV2", "UXG-Pro" },
        { "UXGLITE", "UXG-Lite" },
        { "UXGFIBER", "UXG-Fiber" },
        { "UXGENT", "UXG-Enterprise" },
        { "UXGB", "UXG-Max" },
        { "UXGA6AA", "UXG-Max" },

        // Dream Routers
        { "UDR", "UDR" },
        { "UDR7", "UDR7" },
        { "UDR5G", "UDR-5G-Max" },
        { "UDRULT", "UCG-Ultra" },
        { "UDMA67A", "UDR" },
        { "UDMA6B9", "UDR" },

        // UniFi Express
        { "UX", "UX" },
        { "EXPRESS", "UX" },
        { "UX7", "UX7" },
        { "UDMA69B", "UX" },
        { "UXMAX", "UX-Max" },

        // =====================================================================
        // SWITCHES
        // =====================================================================

        // ----- USW Flex Series (MIPS - no iperf3) -----
        { "USWFLEX", "USW-Flex" },
        { "USF5P", "USW-Flex" },                      // 5-port Flex
        { "USWFLEXMINI", "USW-Flex-Mini" },
        { "USW-FLEX-MINI", "USW-Flex-Mini" },
        { "USMINI", "USW-Flex-Mini" },
        { "USMINI2", "USW-Flex-Mini" },               // Rev 2
        { "USFXG", "USW-Flex-XG" },

        // ----- USW Flex 2.5G Series (MIPS - no iperf3) -----
        { "USM25G5", "USW-Flex-2.5G-5" },
        { "USWED35", "USW-Flex-2.5G-5" },             // Hardware revision
        { "USM25G8", "USW-Flex-2.5G-8" },
        { "USWED36", "USW-Flex-2.5G-8" },             // Hardware revision
        { "USM25G8P", "USW-Flex-2.5G-8-PoE" },
        { "USWED37", "USW-Flex-2.5G-8-PoE" },         // Hardware revision

        // ----- USW Ultra Series (MIPS - no iperf3) -----
        { "USWULTRA", "USW-Ultra" },
        { "USM8P", "USW-Ultra" },
        { "USM8P60", "USW-Ultra-60W" },
        { "USM8P210", "USW-Ultra-210W" },

        // ----- USW Lite Series -----
        { "USWLITE8", "USW-Lite-8-PoE" },
        { "USWLITE16", "USW-Lite-16-PoE" },
        { "USL8LP", "USW-Lite-8-PoE" },
        { "USL8LPB", "USW-Lite-8-PoE" },              // Hardware revision B
        { "USL16LP", "USW-Lite-16-PoE" },
        { "USL16LPB", "USW-Lite-16-PoE" },            // Hardware revision B

        // ----- USW Mission Critical Series -----
        { "USL8MP", "USW-Mission-Critical" },

        // ----- USW Standard Series (Gen2) -----
        { "USW8", "USW-8" },
        { "USC8", "USW-8" },
        { "USW8P60", "USW-8-60W" },
        { "USC8P60", "USW-8-60W" },
        { "USW8P150", "USW-8-150W" },
        { "USC8P150", "USW-8-150W" },
        { "USC8P450", "USW-Industrial" },
        { "USW16P150", "USW-16-PoE" },
        { "USL16P", "USW-16-PoE" },
        { "USL16PB", "USW-16-PoE" },                  // Hardware revision B
        { "USW24", "USW-24" },
        { "USL24", "USW-24" },
        { "USL24B", "USW-24" },                       // Hardware revision B
        { "USW24P250", "USW-24-PoE" },
        { "USL24P", "USW-24-PoE" },
        { "USL24PB", "USW-24-PoE" },                  // Hardware revision B
        { "USW24P500", "USW-24-PoE-500W" },
        { "USW48", "USW-48" },
        { "USL48", "USW-48" },
        { "USL48B", "USW-48" },                       // Hardware revision B
        { "USW48P500", "USW-48-PoE" },
        { "USL48P", "USW-48-PoE" },
        { "USL48PB", "USW-48-PoE" },                  // Hardware revision B
        { "USW48P750", "USW-48-PoE-750W" },

        // ----- USW Pro Series -----
        { "USWPRO24", "USW-Pro-24" },
        { "US24PRO2", "USW-Pro-24" },
        { "USWPRO24POE", "USW-Pro-24-PoE" },
        { "US24PRO", "USW-Pro-24-PoE" },
        { "US24P250", "USW-Pro-24-PoE" },
        { "US24P500", "USW-Pro-24-PoE-500W" },
        { "USWPRO48", "USW-Pro-48" },
        { "US48PRO2", "USW-Pro-48" },
        { "USWPRO48POE", "USW-Pro-48-PoE" },
        { "US48PRO", "USW-Pro-48-PoE" },
        { "US48P500", "USW-Pro-48-PoE" },
        { "US48P750", "USW-Pro-48-PoE-750W" },

        // ----- USW Pro Max Series -----
        { "USPM16", "USW-Pro-Max-16" },
        { "USPM16P", "USW-Pro-Max-16-PoE" },
        { "USPM24", "USW-Pro-Max-24" },
        { "USPM24P", "USW-Pro-Max-24-PoE" },
        { "USPM48", "USW-Pro-Max-48" },
        { "USPM48P", "USW-Pro-Max-48-PoE" },

        // ----- USW Pro XG Series -----
        { "USPXG8P", "USW-Pro-XG-8-PoE" },
        { "USWED76", "USW-Pro-XG-8-PoE" },
        { "USPXG10P", "USW-Pro-XG-10-PoE" },
        { "USWED77", "USW-Pro-XG-10-PoE" },
        { "USWPXG24", "USW-Pro-XG-24" },
        { "USWPXG24P", "USW-Pro-XG-24-PoE" },
        { "USWPXG48", "USW-Pro-XG-48" },
        { "USWPXG48P", "USW-Pro-XG-48-PoE" },
        { "USPH24", "USW-Pro-XG-24" },

        // ----- USW XP Series (PoE variants) -----
        { "USLP8P", "USW-Pro-8-PoE" },
        { "USLP24P", "USW-XP-24-PoE" },
        { "USLP48P", "USW-XP-48-PoE" },

        // ----- USW L2 Series -----
        { "US24PL2", "USW-L2-24-PoE" },
        { "US48PL2", "USW-L2-48-PoE" },

        // ----- USW Enterprise Series -----
        { "US68P", "USW-Enterprise-8-PoE" },
        { "USWENTERPRISE8POE", "USW-Enterprise-8-PoE" },
        { "USWED44", "USW-Enterprise-8-PoE" },
        { "US624P", "USW-Enterprise-24-PoE" },
        { "USWENTERPRISE24POE", "USW-Enterprise-24-PoE" },
        { "USWED42", "USW-Enterprise-24-PoE" },
        { "US648P", "USW-Enterprise-48-PoE" },
        { "USWENTERPRISE48POE", "USW-Enterprise-48-PoE" },
        { "USWED43", "USW-Enterprise-48-PoE" },
        { "USWENTERPRISEXG24", "USW-EnterpriseXG-24" },
        { "USXG24", "USW-EnterpriseXG-24" },
        { "USWED45", "USW-EnterpriseXG-24" },

        // ----- USW Aggregation Series -----
        { "USWAGGREGATION", "USW-Aggregation" },
        { "USL8A", "USW-Aggregation" },
        { "USWAGGPRO", "USW-Pro-Aggregation" },
        { "USAGGPRO", "USW-Pro-Aggregation" },
        { "US16XG", "USW-16-XG" },
        { "USXG", "USW-16-XG" },
        { "US6XG150", "US-XG-6PoE" },

        // ----- Enterprise Campus Series -----
        { "EAS24", "ECS-24" },
        { "EAS24P", "ECS-24-PoE" },
        { "EAS48", "ECS-48" },
        { "EAS48P", "ECS-48-PoE" },
        { "ECS-AGG", "ECS-Aggregation" },
        { "ECSAGG", "ECS-Aggregation" },
        { "USWF064", "ECS-Aggregation" },
        { "USWF066", "ECS-Aggregation" },
        { "ESWHS", "ECS-Aggregation" },

        // ----- Data Center / Leaf Switches -----
        { "UDC48X6", "USW-Leaf" },
        { "USW-LEAF", "USW-Leaf" },

        // ----- US (Gen1/Older) Switches -----
        { "US8", "US-8" },
        { "US8P60", "US-8-60W" },
        { "US8P150", "US-8-150W" },
        { "S28150", "US-8-150W" },
        { "US16P150", "US-16-150W" },
        { "S216150", "US-16-150W" },
        { "US24", "US-24" },
        { "S224250", "US-24-250W" },
        { "S224500", "US-24-500W" },
        { "US48", "US-48" },
        { "S248500", "US-48-500W" },
        { "S248750", "US-48-750W" },

        // ----- Power Distribution / RPS -----
        { "USPPDUP", "USP-PDU-Pro" },
        { "USPPDUHD", "USP-PDU-HD" },
        { "USPRPS", "USP-RPS" },
        { "USPRPSP", "USP-RPS" },

        // ----- Hardware Revision Codes (USWED/USWF series - map to best guess) -----
        { "USWED72", "USW-Pro-HD-24-PoE" },
        { "USWED73", "USW-Pro-HD-24" },
        { "USWF067", "USW-Pro" },
        { "USWF068", "USW-Pro" },
        { "USWF069", "USW-Pro" },
        { "USWF070", "USW-Pro" },
        { "WRS3", "USW-Pro" },
        { "WRS3F", "USW-Pro" },

        // =====================================================================
        // ACCESS POINTS
        // =====================================================================

        // ----- WiFi 7 (U7) Series -----
        { "U7PRO", "U7-Pro" },
        { "U7PROMAX", "U7-Pro-Max" },
        { "U7PROMAXB", "U7-Pro-Max" },
        { "U7ENT", "U7-Pro-Max" },
        { "U7PROXGSB", "U7-Pro-XGS-B" },
        { "U7PROXGS", "U7-Pro-XGS" },
        { "U7PROXGB", "U7-Pro-XG-B" },
        { "U7PROXG", "U7-Pro-XG" },
        { "U7PIW", "U7-Pro-Wall" },
        { "U7PO", "U7-Pro-Outdoor" },
        { "U7POEU", "U7-Pro-Outdoor" },
        // Note: G7* are internal short codes for WiFi 7 APs
        { "G7LR", "U7-LR" },
        { "G7LRV2", "U7-LR" },
        { "G7LT", "U7-Lite" },
        { "G7IW", "U7-IW" },
        { "UKPW", "U7-Outdoor" },

        // ----- Enterprise WiFi 7 (E7) Series -----
        { "E7", "E7" },
        { "E7CEU", "E7" },
        { "E7CAMPUS", "E7-Campus" },
        { "E7AUDIENCE", "E7-Audience" },
        { "E7AUDEU", "E7-Audience" },

        // ----- WiFi 6E (U6E) Series -----
        { "U6ENTERPRISEB", "U6-Enterprise" },
        { "U6ENT", "U6-Enterprise" },
        { "U6ENTERPRISEINWALL", "U6-Enterprise-IW" },
        { "U6ENTIW", "U6-Enterprise-IW" },
        { "U6MESH", "U6-Mesh" },
        { "U6M", "U6-Mesh" },

        // ----- WiFi 6 (U6) Series -----
        { "U6PRO", "U6-Pro" },
        { "UAP6MP", "U6-Pro" },
        { "U6MP", "U6-Pro" },
        { "U6LR", "U6-LR" },
        { "UALR6", "U6-LR" },
        { "UALR6V2", "U6-LR" },
        { "UALR6V3", "U6-LR" },
        { "UAP6", "U6-LR" },
        { "UALRPL6", "U6-PLUS-LR" },
        { "U6LITE", "U6-Lite" },
        { "UAL6", "U6-Lite" },
        { "U6PLUS", "U6+" },
        { "UAPL6", "U6+" },
        { "U6EXTENDER", "U6-Extender" },
        { "U6EXT", "U6-Extender" },
        { "UAE6", "U6-Extender" },
        { "U6IW", "U6-IW" },
        { "UAIW6", "U6-IW" },
        { "UAM6", "U6-Mesh" },

        // ----- AC Wave 2 / HD Series -----
        { "U7HD", "UAP-AC-HD" },
        { "UAPHD", "UAP-HD" },
        { "U7SHD", "UAP-AC-SHD" },
        { "UAPSHD", "UAP-SHD" },
        { "U7NHD", "UAP-nanoHD" },
        { "UAPNANOHD", "UAP-nanoHD" },
        { "UFLHD", "UAP-FlexHD" },
        { "UFLEXHD", "UAP-FlexHD" },
        { "UHDIW", "UAP-IW-HD" },
        { "U7E", "UAP-AC" },
        { "U7EV2", "UAP-AC" },
        { "U7EDU", "UAP-AC-EDU" },

        // ----- AC Series -----
        { "UAPPRO", "UAP-AC-Pro" },
        { "U7PG2", "UAP-AC-Pro" },
        { "U7P", "UAP-AC-Pro" },
        { "UAPLR", "UAP-AC-LR" },
        { "U7LR", "UAP-AC-LR" },
        { "UAPLITE", "UAP-AC-Lite" },
        { "U7LT", "UAP-AC-Lite" },
        { "UAPM", "UAP-AC-M" },
        { "UAPMESH", "UAP-AC-Mesh" },
        { "U7MSH", "UAP-AC-Mesh" },
        { "UAPMESHPRO", "UAP-AC-Mesh-Pro" },
        { "U7MP", "UAP-AC-Mesh-Pro" },
        { "UAPIW", "UAP-AC-IW" },
        { "U7IW", "UAP-AC-IW" },
        { "UAPIWPRO", "UAP-AC-IW-Pro" },
        { "U7IWP", "UAP-AC-IW-Pro" },
        { "U7O", "UAP-AC-Outdoor" },
        { "U7UKU", "UK-Ultra" },
        { "UAPXG", "UAP-XG" },
        { "UCXG", "UAP-XG" },
        { "UAPBASESTATION", "UAP-BaseStationXG" },
        { "UXSDM", "UWB-XG" },
        { "UXBSDM", "UWB-XG-BK" },

        // ----- Legacy APs (802.11n, MIPS) -----
        { "BZ2", "UAP" },
        { "U2S48", "UAP" },
        { "U2SV2", "UAP" },
        { "BZ2LR", "UAP-LR" },
        { "U2L48", "UAP-LR" },
        { "U2LV2", "UAP-LR" },
        { "U2IW", "UAP-IW" },
        { "U2O", "UAP-Outdoor" },
        { "U2HSR", "UAP-Outdoor+" },
        { "U5O", "UAP-Outdoor5" },
        { "UAP", "UAP" },

        // ----- AP Hardware Revision Codes -----
        { "UAPA697", "U6-Series" },
        { "UAPA693", "U6-Series" },
        { "UAPA6A5", "U6-Series" },
        { "UAPA6A4", "U6-Series" },
        { "UAPA6A9", "U6-Series" },
        { "UAPA6A6", "U6-Series" },
        { "UAPA698", "U6-Series" },
        { "UAPA6B1", "U6-Series" },
        { "UAPA699", "U6-Series" },
        { "UAPA6AB", "U6-Series" },
        { "UAPA6B0", "U6-Series" },
        { "UAPA6B3", "U6-Series" },
        { "UDMB", "UAP-BeaconHD" },

        // =====================================================================
        // OTHER DEVICES
        // =====================================================================

        // ----- UniFi Protect NVRs -----
        { "UNVR", "UNVR" },
        { "UNVR4", "UNVR" },
        { "UNVRPRO", "UNVR-Pro" },
        { "UNVR-PRO", "UNVR-Pro" },

        // ----- UniFi NAS -----
        { "UNASPRO", "UNAS-Pro" },
        { "UNASSTU", "UNAS" },
        { "UNASSTU-BK", "UNAS" },
        { "UNASSTUBK", "UNAS" },

        // ----- UniFi Connect -----
        { "ULED", "UC-LED" },

        // ----- Cellular / LTE -----
        { "U5GMAX", "U5G-Max" },
        { "ULTE", "U-LTE" },
        { "ULTEPRO", "U-LTE-Pro" },
        { "ULTEPUS", "U-LTE-Pro" },
        { "ULTEPEU", "U-LTE-Pro" },

        // ----- Mobile Routers -----
        { "UMR", "UMR" },
        { "UMR-INDUSTRIAL", "UMR-Industrial" },
        { "UMR-ULTRA", "UMR-Ultra" },

        // ----- UPS -----
        { "UPS", "USP" },
        { "UPS2U", "USP-RPS" },

        // ----- Building Bridge -----
        { "UBB", "UBB" },
        { "UBBXG", "UBB-XG" },

        // ----- Device Bridge -----
        { "UDB", "UDB" },
        { "UDBPRO", "UDB-Pro" },
        { "UDBE802", "UDB-Pro" },
        { "UDBPROSECTOR", "UDB-Pro-Sector" },
        { "UACCMPOEAF", "U-POE-af" },

        // ----- Smart Power -----
        { "USP", "USP" },
        { "USPPLUG", "USP-Plug" },
        { "USPSTRIP", "USP-Strip" },
        { "UP1", "USP-Plug" },
        { "UP6", "USP-Strip" },
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
