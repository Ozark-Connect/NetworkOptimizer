namespace NetworkOptimizer.UniFi;

/// <summary>
/// Maps UniFi internal model codes to friendly product names.
/// The UniFi API returns internal codes (model/shortname), but the UI displays
/// friendly names. This database provides the translation.
///
/// Sources:
/// - Ubiquiti's official public.json device database
/// - https://ubntwiki.com/products/software/unifi-controller/api
/// - UniFi device discovery and community documentation
/// </summary>
public static class UniFiProductDatabase
{
    /// <summary>
    /// Model codes for cellular/LTE modems.
    /// Used for auto-discovery in Cellular Modem Settings.
    /// </summary>
    private static readonly HashSet<string> CellularModemModelCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Official codes
        "ULTE",           // U-LTE
        "ULTEPUS",        // U-LTE-Backup-Pro (US)
        "ULTEPEU",        // U-LTE-Backup-Pro (EU)
        "UMBBE630",       // U5G-Max
        "UMBBE631",       // U5G-Max-Outdoor

        // Legacy/alternate codes
        "U5GMAX",         // U5G-Max (legacy)
        "ULTEPRO",        // U-LTE (legacy)
    };

    /// <summary>
    /// Devices that cannot run iperf3 (used to filter LAN speed test targets).
    /// Includes MIPS-based devices and others that don't ship with iperf3.
    /// </summary>
    private static readonly HashSet<string> DevicesWithoutIperf3 = new(StringComparer.OrdinalIgnoreCase)
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

        // Pro Max Series (no iperf3)
        "USW-Pro-Max-16",
        "USW-Pro-Max-16-PoE",

        // Legacy US Series (MIPS-based)
        "US-8",
        "US-8-60W",
        "US-8-150W",

        // Standard switches (no iperf3)
        "USW-16-PoE",
        "USW-24-PoE",
        "USW-Enterprise-8-PoE",
        "USW-Aggregation",

        // AC APs (no iperf3) - QCA9563 MIPS architecture
        "UAP",
        "UAP-LR",
        "UAP-IW",
        "UAP-Outdoor",
        "UAP-Outdoor+",
        "UAP-Outdoor-5",
        "UAP-AC-Pro",
        "UAP-AC-Lite",
        "UAP-AC-LR",
        "UAP-AC-M",
        "UAP-AC-IW",
        "UAP-AC-EDU",
        "UAP-AC-Outdoor",

        // Device Bridges (no iperf3, except UDB-Switch which may have it)
        "UDB",
        "UDB-Pro",
        "UDB-Pro-Sector",
        "UDB-IoT",

        // UPS and Power devices (no iperf3)
        "UPS-Tower",
        "UPS-2U",
        "USP-PDU-Pro",
        "USP-PDU-HD",
        "USP-RPS",
        "USP-RPS-Pro",
        "USP-Plug",
        "USP-Strip",

        // NAS devices (storage, no iperf3)
        "UNAS-Pro",
        "UNAS-Pro-4",
        "UNAS-Pro-8",
        "UNAS-2-B",
        "UNAS-2-W",
        "UNAS-4-B",
        "UNAS-4-W",
    };

    /// <summary>
    /// Map of official model codes to friendly product names.
    /// These are the primary codes from Ubiquiti's public.json device database.
    /// </summary>
    private static readonly Dictionary<string, string> OfficialModelCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // =====================================================================
        // GATEWAYS / SECURITY GATEWAYS
        // =====================================================================

        // ----- UniFi Dream Machine family -----
        { "UDM", "UDM" },
        { "UDMPRO", "UDM-Pro" },
        { "UDMPROMAX", "UDM-Pro-Max" },
        { "UDMPROSE", "UDM-SE" },

        // ----- Dream Wall -----
        { "UDW", "UDW" },

        // ----- Enterprise Fortress Gateway -----
        { "UDMENT", "EFG" },

        // ----- Cloud Gateways -----
        { "UCGMAX", "UCG-Max" },
        { "UDMA6A8", "UCG-Fiber" },
        { "UDMA6AD", "UCG-Industrial" },

        // ----- Cloud Keys -----
        { "UCK-v3", "UCK" },
        { "UCKG2", "UCK-G2" },
        { "UCKP", "UCK-G2-Plus" },
        { "UCKENT", "CK-Enterprise" },

        // ----- UniFi Security Gateways -----
        { "UGW3", "USG-3P" },
        { "UGW4", "USG-Pro-4" },
        { "UGW8", "UGW8" },
        { "UGWHD4", "USG" },
        { "UGWXG", "USG-XG-8" },

        // ----- UniFi Application Server -----
        { "UASXG", "UAS-XG" },

        // ----- UniFi Gateways (Next-Gen) -----
        { "UXG", "UXG-Lite" },
        { "UXGB", "UXG-Max" },
        { "UXGENT", "UXG-Enterprise" },
        { "UXGPRO", "UXG-Pro" },
        { "UXGA6AA", "UXG-Fiber" },

        // ----- Dream Routers -----
        { "UDR", "UDR" },
        { "UDRULT", "UCG-Ultra" },
        { "UDMA67A", "UDR7" },
        { "UDMA6B9", "UDR-5G-Max" },

        // ----- UniFi Express -----
        { "UX", "UX" },
        { "UDMA69B", "UX7" },

        // =====================================================================
        // SWITCHES
        // =====================================================================

        // ----- Official: USW Flex Series -----
        { "USF5P", "USW-Flex" },
        { "USMINI", "USW-Flex-Mini" },
        { "USMINI2", "USW-Flex-Mini" },
        { "USFXG", "USW-Flex-XG" },

        // ----- Official: USW Flex 2.5G Series -----
        { "USWED35", "USW-Flex-2.5G-5" },
        { "USWED36", "USW-Flex-2.5G-8" },
        { "USWED37", "USW-Flex-2.5G-8-PoE" },

        // ----- Official: USW Ultra Series -----
        { "USM8P", "USW-Ultra" },
        { "USM8P60", "USW-Ultra-60W" },
        { "USM8P210", "USW-Ultra-210W" },

        // ----- Official: USW Lite Series -----
        { "USL8LP", "USW-Lite-8-PoE" },
        { "USL8LPB", "USW-Lite-8-PoE" },
        { "USL16LP", "USW-Lite-16-PoE" },
        { "USL16LPB", "USW-Lite-16-PoE" },

        // ----- Official: USW Mission Critical Series -----
        { "USL8MP", "USW-Mission-Critical" },

        // ----- Official: USW Standard Series -----
        { "US8", "US-8" },
        { "USC8", "US-8" },
        { "USC8P60", "US-8-60W" },
        { "USC8P150", "US-8-150W" },
        { "USC8P450", "USW-Industrial" },
        { "USL16P", "USW-16-PoE" },
        { "USL16PB", "USW-16-PoE" },
        { "USL24", "USW-24" },
        { "USL24B", "USW-24" },
        { "USL24P", "USW-24-PoE" },
        { "USL24PB", "USW-24-PoE" },
        { "USL48", "USW-48" },
        { "USL48B", "USW-48" },
        { "USL48P", "USW-48-PoE" },
        { "USL48PB", "USW-48-PoE" },

        // ----- Official: USW Pro Series -----
        { "US24PRO2", "USW-Pro-24" },
        { "US48PRO2", "USW-Pro-48" },
        { "US24P250", "US-24-250W" },
        { "US24P500", "US-24-500W" },
        { "US48P500", "US-48-500W" },
        { "US48P750", "US-48-750W" },

        // ----- Official: USW Pro Max Series -----
        { "USPM16", "USW-Pro-Max-16" },
        { "USPM16P", "USW-Pro-Max-16-PoE" },
        { "USPM24", "USW-Pro-Max-24" },
        { "USPM24P", "USW-Pro-Max-24-PoE" },
        { "USPM48", "USW-Pro-Max-48" },
        { "USPM48P", "USW-Pro-Max-48-PoE" },

        // ----- Official: USW Pro XG Series -----
        { "USWED76", "USW-Pro-XG-8-PoE" },
        { "USWED77", "USW-Pro-XG-10-PoE" },
        { "USWED42", "USW-Pro-XG-48-PoE" },
        { "USWED43", "USW-Pro-XG-48" },
        { "USWED44", "USW-Pro-XG-24-PoE" },
        { "USWED45", "USW-Pro-XG-24" },
        { "USWED72", "USW-Pro-HD-24-PoE" },
        { "USWED73", "USW-Pro-HD-24" },

        // ----- Official: USW XP Series -----
        { "USLP8P", "USW-Pro-8-PoE" },
        { "USLP24P", "USW-Pro-24-PoE" },
        { "USLP48P", "USW-Pro-48-PoE" },

        // ----- Official: USW L2 Series -----
        { "US24PL2", "US-L2-24-PoE" },
        { "US48PL2", "US-L2-48-PoE" },

        // ----- Official: USW Enterprise Series -----
        { "US68P", "USW-Enterprise-8-PoE" },
        { "US624P", "USW-Enterprise-24-PoE" },
        { "US648P", "USW-Enterprise-48-PoE" },
        { "USXG24", "USW-EnterpriseXG-24" },

        // ----- Official: USW Aggregation Series -----
        { "USL8A", "USW-Aggregation" },
        { "USAGGPRO", "USW-Pro-Aggregation" },
        { "USXG", "US-16-XG" },
        { "US6XG150", "US-XG-6PoE" },

        // ----- Official: Enterprise Campus Series -----
        { "USWF066", "ECS-Aggregation" },
        { "USWF067", "ECS-24-PoE" },
        { "USWF069", "ECS-48-PoE" },
        { "USWF003", "USW-Pro-XG-Aggregation" },
        { "USWF004", "ECS-24S-PoE" },
        { "USWF006", "ECS-48S-PoE" },

        // ----- Official: Data Center / Leaf Switches -----
        { "UDC48X6", "USW-Leaf" },

        // ----- Official: US Gen1 Switches -----
        { "US16P150", "US-16-150W" },
        { "US24", "US-24-G1" },
        { "US48", "US-48-G1" },

        // ----- Official: WAN Switches -----
        { "USWED05", "USW-Industrial" },
        { "USWED74", "USW-WAN" },
        { "USWED75", "USW-WAN-RJ45" },

        // ----- Power Distribution -----
        { "USPPDUP", "USP-PDU-Pro" },
        { "USPPDUHD", "USP-PDU-HD" },
        { "USPRPS", "USP-RPS" },
        { "USPRPSP", "USP-RPS-Pro" },

        // =====================================================================
        // ACCESS POINTS
        // =====================================================================

        // ----- Official: WiFi 7 (U7) Series -----
        { "U7PRO", "U7-Pro" },
        { "U7PROMAX", "U7-Pro-Max" },
        { "U7ENT", "U7-Pro-Max" },
        { "U7PIW", "U7-Pro-Wall" },
        { "UKPW", "U7-Outdoor" },

        // ----- Official: WiFi 7 Hardware Revision Codes -----
        { "UAPA693", "U7-Lite" },
        { "UAPA69E", "U7-Mesh" },
        { "UAPA6A4", "U7-Pro-XGS" },
        { "UAPA6A5", "U7-IW" },
        { "UAPA6A6", "U7-Pro-Outdoor" },
        { "UAPA6A9", "U7-Pro-XG" },
        { "UAPA6AC", "U7-Pro-XGS-B" },
        { "UAPA6AE", "U7-Pro-XG-B" },
        { "UAPA6B0", "U7-Pro-Outdoor-EU" },
        { "UAPA6B3", "U7-LR" },
        { "UAPA6BA", "U7-Pro-XG-Wall" },

        // ----- Official: Enterprise WiFi 7 (E7) Series -----
        { "UAPA697", "E7" },
        { "UAPA698", "E7-Campus" },
        { "UAPA699", "E7-Audience" },
        { "UAPA6AB", "E7-Audience-EU" },
        { "UAPA6AF", "E7-Audience-Indoor" },
        { "UAPA6B1", "E7-Campus-EU" },
        { "UAPA6BC", "E7-Campus-Indoor" },

        // ----- Official: WiFi 6E/6 Series -----
        { "U6ENT", "U6-Enterprise" },
        { "U6ENTIW", "U6-Enterprise-IW" },
        { "U6M", "U6-Mesh" },
        { "U6MP", "U6-Mesh-Pro" },
        { "U6EXT", "U6-Extender" },
        { "U6IW", "U6-IW" },
        { "UAE6", "U6-Extender" },
        { "UAL6", "U6-Lite" },
        { "UALR6", "U6-LR" },
        { "UALR6v2", "U6-LR" },
        { "UALR6v3", "U6-LR" },
        { "UALRPL6", "U6-PLUS-LR" },
        { "UAM6", "U6-Mesh" },
        { "UAP6MP", "U6-Pro" },
        { "UAPL6", "U6+" },
        { "UAIW6", "U6-IW" },

        // ----- Official: AC Wave 2 / HD Series -----
        { "U7HD", "UAP-AC-HD" },
        { "U7SHD", "UAP-AC-SHD" },
        { "U7NHD", "UAP-nanoHD" },
        { "U7EDU", "UAP-AC-EDU" },
        { "U7Ev2", "UAP-AC" },
        { "UFLHD", "UAP-FlexHD" },
        { "UHDIW", "UAP-IW-HD" },
        { "UCXG", "UAP-XG" },
        { "UXSDM", "UWB-XG" },
        { "UXBSDM", "UWB-XG-BK" },

        // ----- Official: AC Series -----
        { "U7PG2", "UAP-AC-Pro" },
        { "U7P", "UAP-Pro" },
        { "U7LR", "UAP-AC-LR" },
        { "U7LT", "UAP-AC-Lite" },
        { "U7MSH", "UAP-AC-M" },
        { "U7MP", "UAP-AC-M-PRO" },
        { "U7IW", "UAP-AC-IW" },
        { "U7IWP", "UAP-AC-IW-Pro" },
        { "U7O", "UAP-AC-Outdoor" },
        { "U7UKU", "UK-Ultra" },

        // ----- Official: Legacy APs (802.11n) -----
        { "U2S48", "UAP" },
        { "U2Sv2", "UAPv2" },
        { "U2L48", "UAP-LR" },
        { "U2Lv2", "UAP-LRv2" },
        { "U2IW", "UAP-IW" },
        { "U2O", "UAP-Outdoor" },
        { "U2HSR", "UAP-Outdoor+" },
        { "U5O", "UAP-Outdoor-5" },

        // ----- BeaconHD -----
        { "UDMB", "UAP-BeaconHD" },

        // =====================================================================
        // OTHER DEVICES
        // =====================================================================

        // ----- Official: UniFi Protect NVRs -----
        { "ENVR", "ENVR" },
        { "UNVR4", "UNVR" },
        { "UNVRPRO", "UNVR-Pro" },
        { "UNVRINS", "UNVR-Instant" },

        // ----- Official: UniFi NAS -----
        { "UNASPRO", "UNAS-Pro" },
        { "UNAS2B", "UNAS-2-B" },
        { "UNAS2W", "UNAS-2-W" },
        { "UNASEA63", "UNAS-Pro-8" },
        { "UNASEA65", "UNAS-4-W" },
        { "UNASEA66", "UNAS-4-B" },
        { "UNASEA67", "UNAS-Pro-4" },

        // ----- Official: Cellular / LTE -----
        { "ULTE", "U-LTE" },
        { "ULTEPUS", "U-LTE-Backup-Pro" },
        { "ULTEPEU", "U-LTE-Backup-Pro" },
        { "UCI", "UCI" },
        { "UMBBE630", "U5G-Max" },
        { "UMBBE631", "U5G-Max-Outdoor" },

        // ----- Official: UPS -----
        { "USWDA23", "UPS-Tower" },
        { "USWDA24", "UPS-Tower" },
        { "USWDA25", "UPS-2U" },
        { "USWDA26", "UPS-2U" },

        // ----- Official: Building Bridge -----
        { "UBB", "UBB" },
        { "UBBXG", "UBB-XG" },

        // ----- Official: Device Bridge -----
        { "UDB", "UDB-Pro" },
        { "UDBE802", "UDB-Pro-Sector" },
        { "UACCMPOEAF", "UDB" },
        { "UACCEA03", "UDB-IoT" },
        { "UDBA69F", "UDB-Switch" },

        // ----- Official: Smart Power -----
        { "UP1", "USP-Plug" },
        { "UP6", "USP-Strip" },

        // ----- Official: VoIP Phones -----
        { "UP4", "UVP-X" },
        { "UP5c", "UVP" },
        { "UP5tc", "UVP-Pro" },
        { "UP7c", "UVP-Executive" },

        // ----- Other -----
        { "USFPW", "UACC-SFP-Wizard" },
        { "UTREA06", "UTR" },
        { "p2N", "PICOM2HP" },
    };

    /// <summary>
    /// Legacy/alternate codes for shortname-based lookup.
    /// These are kept for compatibility with older firmware or alternate
    /// API responses. They map to the same products as official codes.
    /// Used only when the official model code lookup fails.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyShortnameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // =====================================================================
        // GATEWAYS
        // =====================================================================
        { "UDM-PRO", "UDM-Pro" },
        { "UDM-PRO-SE", "UDM-SE" },
        { "UDM-PRO-MAX", "UDM-Pro-Max" },
        { "UDMSE", "UDM-SE" },
        { "EFG", "EFG" },
        { "UCGF", "UCG-Fiber" },
        { "UCG-ULTRA", "UCG-Ultra" },
        { "UCG-INDUSTRIAL", "UCG-Industrial" },
        { "UCGA6AD", "UCG-Industrial" },
        { "UCK-G2", "UCK-G2" },
        { "UCK-G2-PLUS", "UCK-G2-Plus" },
        { "UCKP2", "UCK-G2-Plus" },
        { "USG", "USG" },
        { "UGW", "USG" },
        { "UXG-PRO", "UXG-Pro" },
        { "UXGPROV2", "UXG-Pro" },
        { "UXGLITE", "UXG-Lite" },
        { "UXGFIBER", "UXG-Fiber" },
        { "UDR7", "UDR7" },
        { "UDR5G", "UDR-5G-Max" },
        { "EXPRESS", "UX" },
        { "UX7", "UX7" },
        { "UXMAX", "UX7" },

        // =====================================================================
        // SWITCHES
        // =====================================================================
        { "USWFLEX", "USW-Flex" },
        { "USWFLEXMINI", "USW-Flex-Mini" },
        { "USW-FLEX-MINI", "USW-Flex-Mini" },
        { "USM25G5", "USW-Flex-2.5G-5" },
        { "USM25G8", "USW-Flex-2.5G-8" },
        { "USM25G8P", "USW-Flex-2.5G-8-PoE" },
        { "USWULTRA", "USW-Ultra" },
        { "USWLITE8", "USW-Lite-8-PoE" },
        { "USWLITE16", "USW-Lite-16-PoE" },
        { "USW8", "US-8" },
        { "USW8P60", "US-8-60W" },
        { "USW8P150", "US-8-150W" },
        { "US8P60", "US-8-60W" },
        { "US8P150", "US-8-150W" },
        { "USW16P150", "USW-16-PoE" },
        { "USW24", "USW-24" },
        { "USW24P250", "USW-24-PoE" },
        { "USW48", "USW-48" },
        { "USW48P500", "USW-48-PoE" },
        { "USWPRO24", "USW-Pro-24" },
        { "USWPRO24POE", "USW-Pro-24-PoE" },
        { "US24PRO", "USW-Pro-24-PoE" },
        { "USWPRO48", "USW-Pro-48" },
        { "USWPRO48POE", "USW-Pro-48-PoE" },
        { "US48PRO", "USW-Pro-48-PoE" },
        { "USPXG8P", "USW-Pro-XG-8-PoE" },
        { "USPXG10P", "USW-Pro-XG-10-PoE" },
        { "USWPXG24", "USW-Pro-XG-24" },
        { "USWPXG24P", "USW-Pro-XG-24-PoE" },
        { "USWPXG48", "USW-Pro-XG-48" },
        { "USWPXG48P", "USW-Pro-XG-48-PoE" },
        { "USPH24", "USW-Pro-XG-24" },
        { "USWENTERPRISE8POE", "USW-Enterprise-8-PoE" },
        { "USWENTERPRISE24POE", "USW-Enterprise-24-PoE" },
        { "USWENTERPRISE48POE", "USW-Enterprise-48-PoE" },
        { "USWENTERPRISEXG24", "USW-EnterpriseXG-24" },
        { "USWAGGREGATION", "USW-Aggregation" },
        { "USWAGGPRO", "USW-Pro-Aggregation" },
        { "US16XG", "US-16-XG" },
        { "EAS24", "ECS-24-PoE" },
        { "EAS24P", "ECS-24-PoE" },
        { "EAS48", "ECS-48-PoE" },
        { "EAS48P", "ECS-48-PoE" },
        { "ECS-AGG", "ECS-Aggregation" },
        { "ECSAGG", "ECS-Aggregation" },
        { "USWF064", "ECS-Aggregation" },
        { "ESWHS", "ECS-Aggregation" },
        { "USW-LEAF", "USW-Leaf" },
        { "S28150", "US-8-150W" },
        { "S216150", "US-16-150W" },
        { "S224250", "US-24-250W" },
        { "S224500", "US-24-500W" },
        { "S248500", "US-48-500W" },
        { "S248750", "US-48-750W" },
        { "USWF068", "USW-Pro-24" },
        { "USWF070", "USW-Pro-24" },
        { "WRS3", "USW-Pro-24" },
        { "WRS3F", "USW-Pro-24" },
        { "UPS2U", "USP-RPS" },

        // =====================================================================
        // ACCESS POINTS
        // =====================================================================
        { "U7PROMAXB", "U7-Pro-Max" },
        { "U7PROXGSB", "U7-Pro-XGS-B" },
        { "U7PROXGS", "U7-Pro-XGS" },
        { "U7PROXGB", "U7-Pro-XG-B" },
        { "U7PROXG", "U7-Pro-XG" },
        { "U7PO", "U7-Pro-Outdoor" },
        { "U7POEU", "U7-Pro-Outdoor-EU" },
        { "G7LR", "U7-LR" },
        { "G7LRV2", "U7-LR" },
        { "G7LT", "U7-Lite" },
        { "U7MESH", "U7-Mesh" },
        { "U7-MESH", "U7-Mesh" },
        { "G7IW", "U7-IW" },
        { "E7", "E7" },
        { "E7CEU", "E7-Campus-EU" },
        { "E7CAMPUS", "E7-Campus" },
        { "E7AUDIENCE", "E7-Audience" },
        { "E7AEU", "E7-Audience-EU" },
        { "E7AUDEU", "E7-Audience-EU" },
        { "U6ENTERPRISEB", "U6-Enterprise" },
        { "U6ENTERPRISEINWALL", "U6-Enterprise-IW" },
        { "U6MESH", "U6-Mesh" },
        { "U6PRO", "U6-Pro" },
        { "U6LR", "U6-LR" },
        { "UAP6", "U6-LR" },
        { "U6LITE", "U6-Lite" },
        { "U6PLUS", "U6+" },
        { "U6EXTENDER", "U6-Extender" },
        { "UAPHD", "UAP-AC-HD" },
        { "UAPSHD", "UAP-AC-SHD" },
        { "UAPNANOHD", "UAP-nanoHD" },
        { "UFLEXHD", "UAP-FlexHD" },
        { "U7E", "UAP-AC" },
        { "UAPPRO", "UAP-AC-Pro" },
        { "UAPLR", "UAP-AC-LR" },
        { "UAPLITE", "UAP-AC-Lite" },
        { "UAPM", "UAP-AC-M" },
        { "UAPMESH", "UAP-AC-M" },
        { "UAPMESHPRO", "UAP-AC-M-PRO" },
        { "UAPIW", "UAP-AC-IW" },
        { "UAPIWPRO", "UAP-AC-IW-Pro" },
        { "UAPXG", "UAP-XG" },
        { "UAPBASESTATION", "UAP-XG" },
        { "BZ2", "UAP" },
        { "BZ2LR", "UAP-LR" },
        { "UAP", "UAP" },

        // =====================================================================
        // OTHER DEVICES
        // =====================================================================
        { "UNVR", "UNVR" },
        { "UNVR-PRO", "UNVR-Pro" },
        { "U5GMAX", "U5G-Max" },
        { "ULTEPRO", "U-LTE" },
        { "UDBPRO", "UDB-Pro" },
        { "UDBPROSECTOR", "UDB-Pro-Sector" },
        { "USPPLUG", "USP-Plug" },
        { "USPSTRIP", "USP-Strip" },
    };

    /// <summary>
    /// Get the friendly product name for an official model code.
    /// </summary>
    /// <param name="modelCode">The model code from the UniFi API</param>
    /// <returns>Friendly product name, or the original code if not found</returns>
    public static string GetProductName(string? modelCode)
    {
        if (string.IsNullOrEmpty(modelCode))
            return "Unknown";

        // Official model code lookup only
        if (OfficialModelCodes.TryGetValue(modelCode, out var name))
            return name;

        // Return original if not found - this helps identify new models
        return modelCode;
    }

    /// <summary>
    /// Get the friendly product name from a shortname using legacy/alternate codes
    /// </summary>
    /// <param name="shortname">The shortname from the UniFi API</param>
    /// <returns>Friendly product name, or the original shortname if not found</returns>
    public static string GetProductNameFromShortname(string? shortname)
    {
        if (string.IsNullOrEmpty(shortname))
            return "Unknown";

        // Try legacy shortname alias lookup
        if (LegacyShortnameAliases.TryGetValue(shortname, out var name))
            return name;

        // Return original if not found
        return shortname;
    }

    /// <summary>
    /// Get the best available product name from multiple fields.
    /// Checks official model codes first, then legacy shortname aliases.
    /// </summary>
    /// <param name="model">The model field (internal code)</param>
    /// <param name="shortname">The shortname field</param>
    /// <returns>Best available friendly name</returns>
    public static string GetBestProductName(string? model, string? shortname)
    {
        // Try official model code lookup first (preferred)
        var modelLookup = GetProductName(model);
        if (!string.IsNullOrEmpty(model) && modelLookup != model)
            return modelLookup;

        // Try legacy shortname alias lookup
        var shortnameLookup = GetProductNameFromShortname(shortname);
        if (!string.IsNullOrEmpty(shortname) && shortnameLookup != shortname)
            return shortnameLookup;

        // Fall back to shortname, then model
        return shortname ?? model ?? "Unknown";
    }

    /// <summary>
    /// Check if a device can run iperf3 for LAN speed testing
    /// </summary>
    /// <param name="productName">The friendly product name (e.g., "USW-Flex-Mini")</param>
    /// <returns>True if the device supports iperf3</returns>
    public static bool CanRunIperf3(string? productName)
    {
        if (string.IsNullOrEmpty(productName))
            return true;

        return !DevicesWithoutIperf3.Contains(productName);
    }

    /// <summary>
    /// Check if a device can run iperf3 using multiple identification fields
    /// </summary>
    /// <param name="model">The model field (internal code)</param>
    /// <param name="shortname">The shortname field</param>
    /// <returns>True if the device supports iperf3</returns>
    public static bool CanRunIperf3(string? model, string? shortname)
    {
        var productName = GetBestProductName(model, shortname);
        return CanRunIperf3(productName);
    }

    /// <summary>
    /// Check if a model code represents a cellular/LTE modem
    /// </summary>
    /// <param name="modelCode">The model or shortname from the UniFi API</param>
    /// <returns>True if the device is a cellular modem</returns>
    public static bool IsCellularModem(string? modelCode)
    {
        if (string.IsNullOrEmpty(modelCode))
            return false;

        return CellularModemModelCodes.Contains(modelCode);
    }

    /// <summary>
    /// Check if a device is a cellular/LTE modem using multiple identification fields
    /// </summary>
    /// <param name="model">The model field (internal code)</param>
    /// <param name="shortname">The shortname field</param>
    /// <param name="deviceType">The type field from UniFi API (e.g., "umbb" for modems)</param>
    /// <returns>True if the device is a cellular modem</returns>
    public static bool IsCellularModem(string? model, string? shortname, string? deviceType)
    {
        // Check model code first
        if (IsCellularModem(model))
            return true;

        // Check shortname
        if (IsCellularModem(shortname))
            return true;

        // Check device type - "umbb" is the UniFi type for mobile broadband devices
        if (!string.IsNullOrEmpty(deviceType) &&
            deviceType.Equals("umbb", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Get the default QMI device path for a cellular modem based on its model.
    /// U-LTE devices use /dev/cdc-wdm0, U5G devices use /dev/wwan0qmi0.
    /// </summary>
    /// <param name="model">The model code or product name</param>
    /// <returns>The default QMI device path for this modem type</returns>
    public static string GetDefaultQmiDevicePath(string? model)
    {
        if (string.IsNullOrEmpty(model))
            return "/dev/wwan0qmi0";

        // U-LTE devices (including U-LTE-Pro, U-LTE-Backup-Pro) use /dev/cdc-wdm0
        // Check model codes: ULTE, ULTEPUS, ULTEPEU, ULTEPRO
        // Also check product names: U-LTE, U-LTE-Backup-Pro
        if (model.StartsWith("ULTE", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("U-LTE", StringComparison.OrdinalIgnoreCase))
        {
            return "/dev/cdc-wdm0";
        }

        // U5G-Max and other 5G modems use /dev/wwan0qmi0
        return "/dev/wwan0qmi0";
    }
}
