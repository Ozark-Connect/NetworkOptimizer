using System.Text.RegularExpressions;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Shared helper for determining if a port has a default (system-assigned) name
/// vs a custom (user-assigned) name.
/// </summary>
public static class PortNameHelper
{
    /// <summary>
    /// Pattern matching default/system port names.
    /// Covers: Port 1, SFP 1, SFP+ 1, SFP28 1, SFP56 1, QSFP28 1, QSFP+ 1, QSFP56 1, bare numbers
    /// </summary>
    private static readonly Regex DefaultPortNamePattern = new(
        @"^(Port\s*\d+|Q?SFP(\+|28|56)?\s*\d+|\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Check if a port name is a default/system name (not user-customized).
    /// </summary>
    /// <param name="portName">The port name to check</param>
    /// <returns>True if the name is a default pattern like "Port 1", "SFP+ 2", etc.</returns>
    public static bool IsDefaultPortName(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            return true;

        return DefaultPortNamePattern.IsMatch(portName.Trim());
    }

    /// <summary>
    /// Check if a port has a custom (user-assigned) name.
    /// </summary>
    /// <param name="portName">The port name to check</param>
    /// <returns>True if the name is custom (e.g., "Printer", "Server Room Camera")</returns>
    public static bool IsCustomPortName(string? portName)
    {
        return !IsDefaultPortName(portName);
    }
}
