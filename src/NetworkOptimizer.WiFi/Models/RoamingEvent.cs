namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// A client roaming event between access points
/// </summary>
public class RoamingEvent
{
    /// <summary>Event timestamp</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Client MAC address</summary>
    public string ClientMac { get; set; } = string.Empty;

    /// <summary>Client name (if known)</summary>
    public string? ClientName { get; set; }

    /// <summary>Source AP MAC (roaming from)</summary>
    public string FromApMac { get; set; } = string.Empty;

    /// <summary>Source AP name</summary>
    public string? FromApName { get; set; }

    /// <summary>Destination AP MAC (roaming to)</summary>
    public string ToApMac { get; set; } = string.Empty;

    /// <summary>Destination AP name</summary>
    public string? ToApName { get; set; }

    /// <summary>Type of roaming transition</summary>
    public RoamingType RoamingType { get; set; }

    /// <summary>Transition duration in milliseconds (if available)</summary>
    public int? TransitionDurationMs { get; set; }

    /// <summary>Signal strength at source AP before roaming (dBm)</summary>
    public int? FromSignal { get; set; }

    /// <summary>Signal strength at destination AP after roaming (dBm)</summary>
    public int? ToSignal { get; set; }

    /// <summary>Signal delta (ToSignal - FromSignal)</summary>
    public int? SignalDelta => ToSignal.HasValue && FromSignal.HasValue
        ? ToSignal.Value - FromSignal.Value
        : null;

    /// <summary>Radio band before roaming</summary>
    public RadioBand? FromBand { get; set; }

    /// <summary>Radio band after roaming</summary>
    public RadioBand? ToBand { get; set; }

    /// <summary>Channel before roaming</summary>
    public int? FromChannel { get; set; }

    /// <summary>Channel after roaming</summary>
    public int? ToChannel { get; set; }

    /// <summary>Whether the roam was successful</summary>
    public bool Success { get; set; } = true;

    /// <summary>Reason for roaming (if available from logs)</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Type of roaming transition
/// </summary>
public enum RoamingType
{
    /// <summary>Unknown roaming type</summary>
    Unknown,

    /// <summary>802.11r Fast BSS Transition (fast roaming)</summary>
    FastBssTransition,

    /// <summary>Full re-association (slower)</summary>
    FullReassociation,

    /// <summary>802.11v BSS Transition Management triggered</summary>
    BssTransitionManagement,

    /// <summary>AP-initiated disconnection followed by reconnect</summary>
    ApInitiated,

    /// <summary>Client-initiated roam</summary>
    ClientInitiated
}
