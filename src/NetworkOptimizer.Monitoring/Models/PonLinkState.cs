namespace NetworkOptimizer.Monitoring.Models;

/// <summary>
/// ITU-T G.984.3 / G.9807.1 ONU activation states (O1-O7).
/// </summary>
public enum PonLinkState
{
    Unknown,
    Initial,
    Standby,
    SerialNumber,
    Ranging,
    Operation,
    Popup,
    EmergencyStop,
}

public static class PonLinkStateExtensions
{
    /// <summary>
    /// Parse a raw PON link status string (e.g. "OPERATION (O5)", "O5", "RANGING")
    /// into a typed enum value.
    /// </summary>
    public static PonLinkState ParsePonLinkState(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return PonLinkState.Unknown;

        var s = raw.Trim().ToUpperInvariant();

        if (s.Contains("O1") || s.Contains("INITIAL"))
            return PonLinkState.Initial;
        if (s.Contains("O2") || s.Contains("STANDBY"))
            return PonLinkState.Standby;
        if (s.Contains("O3") || s.Contains("SERIAL"))
            return PonLinkState.SerialNumber;
        if (s.Contains("O4") || s.Contains("RANGING"))
            return PonLinkState.Ranging;
        if (s.Contains("O5") || s.Contains("OPERATION"))
            return PonLinkState.Operation;
        if (s.Contains("O6") || s.Contains("POPUP"))
            return PonLinkState.Popup;
        if (s.Contains("O7") || s.Contains("EMERGENCY"))
            return PonLinkState.EmergencyStop;

        return PonLinkState.Unknown;
    }

    /// <summary>
    /// Human-readable label with ITU state code, e.g. "Connected (O5)".
    /// </summary>
    public static string ToDisplayString(this PonLinkState state) => state switch
    {
        PonLinkState.Initial => "Initializing (O1)",
        PonLinkState.Standby => "Standby (O2)",
        PonLinkState.SerialNumber => "Authenticating (O3)",
        PonLinkState.Ranging => "Ranging (O4)",
        PonLinkState.Operation => "Connected (O5)",
        PonLinkState.Popup => "Signal Lost (O6)",
        PonLinkState.EmergencyStop => "Disabled (O7)",
        _ => "Unknown",
    };

    /// <summary>
    /// Short stable string for InfluxDB storage.
    /// </summary>
    public static string ToInfluxValue(this PonLinkState state) => state switch
    {
        PonLinkState.Initial => "initial",
        PonLinkState.Standby => "standby",
        PonLinkState.SerialNumber => "serial_number",
        PonLinkState.Ranging => "ranging",
        PonLinkState.Operation => "operation",
        PonLinkState.Popup => "popup",
        PonLinkState.EmergencyStop => "emergency_stop",
        _ => "unknown",
    };
}
