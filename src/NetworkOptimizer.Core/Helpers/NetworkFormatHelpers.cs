namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Utility methods for formatting network-related strings
/// </summary>
public static class NetworkFormatHelpers
{
    /// <summary>
    /// Format WAN interface name for display: "wan" -> "WAN1", "wan2" -> "WAN2", etc.
    /// </summary>
    /// <param name="interfaceName">The raw interface name (e.g., "wan", "wan2")</param>
    /// <param name="portName">Optional port name from device configuration</param>
    /// <returns>Formatted display name like "WAN1" or "WAN2 (Fiber)"</returns>
    public static string FormatWanInterfaceName(string interfaceName, string? portName = null)
    {
        // Convert wan/wan2/wan3 to WAN1/WAN2/WAN3
        var formattedName = interfaceName.ToLowerInvariant() switch
        {
            "wan" => "WAN1",
            var name when name.StartsWith("wan") && name.Length > 3 && char.IsDigit(name[3])
                => $"WAN{name[3..]}",
            _ => interfaceName
        };

        // Add port name if available and meaningful
        if (!string.IsNullOrEmpty(portName) && portName != "unnamed")
        {
            return $"{formattedName} ({portName})";
        }

        return formattedName;
    }

    /// <summary>
    /// Determine the PON variant from SFP part number and vendor.
    /// Order matters: XGS-PON checks must come before GPON since "GPON"
    /// is a substring of "XGS-GPON" in some vendor strings.
    /// </summary>
    /// <summary>
    /// Determine the PON variant from SFP part number and vendor.
    /// Sources: hack-gpon.org ONT database, pon.wiki
    /// </summary>
    public static string PonVariantLabel(string? sfpPart, string? sfpVendor)
    {
        var s = $"{sfpPart} {sfpVendor}";
        var p = sfpPart?.Trim() ?? "";
        // Dashless version for matching — vendors inconsistently include/omit dashes
        var pNorm = p.Replace("-", "").Replace("_", "");

        // --- XGS-PON identifiers (check before GPON since "GPON" is a substring) ---

        if (s.Contains("XGS-PON", StringComparison.OrdinalIgnoreCase)
            || s.Contains("XGSPON", StringComparison.OrdinalIgnoreCase)
            || s.Contains("XGS-ONU", StringComparison.OrdinalIgnoreCase)
            || s.Contains("XGSONU", StringComparison.OrdinalIgnoreCase)) return "XGS-PON";

        // WAS-110 and variants (BFW Solutions / Azores)
        if (pNorm.StartsWith("WAS", StringComparison.OrdinalIgnoreCase)) return "XGS-PON";

        // Nokia: XS-010 / XS010 = XGS-PON
        if (pNorm.StartsWith("XS010", StringComparison.OrdinalIgnoreCase)) return "XGS-PON";

        // CIG: XG-99 / XG99 = XGS-PON
        if (pNorm.StartsWith("XG99", StringComparison.OrdinalIgnoreCase)) return "XGS-PON";

        // E.C.I.: EN-XGSFPP / ENXGSFPP = XGS-PON
        if (pNorm.StartsWith("ENXGS", StringComparison.OrdinalIgnoreCase)) return "XGS-PON";

        // HiSense: LTF7267 = XGS-PON
        if (pNorm.StartsWith("LTF7", StringComparison.OrdinalIgnoreCase)) return "XGS-PON";

        // Zyxel: PM without G suffix = XGS-PON (PM7300, PM5100, PM3100)
        if (pNorm.StartsWith("PM", StringComparison.OrdinalIgnoreCase)
            && !pNorm.StartsWith("PMG", StringComparison.OrdinalIgnoreCase)) return "XGS-PON";

        // Contains XGS anywhere
        if (s.Contains("XGS", StringComparison.OrdinalIgnoreCase)) return "XGS-PON";

        if (s.Contains("XG-PON", StringComparison.OrdinalIgnoreCase)
            || s.Contains("XGPON", StringComparison.OrdinalIgnoreCase)) return "XG-PON";

        // --- GPON identifiers ---

        if (s.Contains("GPON", StringComparison.OrdinalIgnoreCase)) return "GPON";

        // Nokia G-010 / G010 = GPON
        if (pNorm.StartsWith("G010", StringComparison.OrdinalIgnoreCase)) return "GPON";
        // Zyxel PMG = GPON
        if (pNorm.StartsWith("PMG", StringComparison.OrdinalIgnoreCase)) return "GPON";
        // CIG G-97 / G97 = GPON
        if (pNorm.StartsWith("G97", StringComparison.OrdinalIgnoreCase)) return "GPON";
        // SourcePhotonics SPS-34 / SPS34 = GPON
        if (pNorm.StartsWith("SPS34", StringComparison.OrdinalIgnoreCase)) return "GPON";
        // ODI DFP-34 / DFP34 = GPON
        if (pNorm.StartsWith("DFP34", StringComparison.OrdinalIgnoreCase)) return "GPON";
        // HALNy HL-GSFP / HLGSFP = GPON
        if (pNorm.StartsWith("HLGSFP", StringComparison.OrdinalIgnoreCase)) return "GPON";

        // --- Other ---
        if (s.Contains("EPON", StringComparison.OrdinalIgnoreCase)) return "EPON";
        if (s.Contains("NG-PON", StringComparison.OrdinalIgnoreCase)
            || s.Contains("NGPON", StringComparison.OrdinalIgnoreCase)) return "NG-PON";

        // Vendor-only fallback: these vendors primarily make PON SFPs.
        // Default to GPON since it's the more common variant; XGS-PON
        // models would have been caught by the prefix checks above.
        if (!string.IsNullOrEmpty(sfpVendor))
        {
            var vNorm = sfpVendor!.Replace("&", "").Replace("-", "").Replace("_", "").Trim();
            if (vNorm.StartsWith("Calix", StringComparison.OrdinalIgnoreCase)) return "GPON";
            if (vNorm.StartsWith("Zyxel", StringComparison.OrdinalIgnoreCase)) return "GPON";
            if (vNorm.StartsWith("Nokia", StringComparison.OrdinalIgnoreCase)) return "GPON";
            if (vNorm.StartsWith("Leox", StringComparison.OrdinalIgnoreCase)) return "GPON";
            if (vNorm.StartsWith("TW", StringComparison.OrdinalIgnoreCase)) return "GPON";
            if (vNorm.StartsWith("ODI", StringComparison.OrdinalIgnoreCase)) return "GPON";
            if (vNorm.StartsWith("HALNy", StringComparison.OrdinalIgnoreCase)) return "GPON";
            if (vNorm.StartsWith("SourcePhotonics", StringComparison.OrdinalIgnoreCase)) return "GPON";
            if (vNorm.StartsWith("CarlitoxxPro", StringComparison.OrdinalIgnoreCase)) return "GPON";
            if (vNorm.StartsWith("FiberMall", StringComparison.OrdinalIgnoreCase)) return "GPON";
        }

        return "PON";
    }
}
