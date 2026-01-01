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
}
