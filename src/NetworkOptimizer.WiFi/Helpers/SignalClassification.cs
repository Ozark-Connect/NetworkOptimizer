using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>
/// Band-aware signal strength classification. Different bands have different noise floors,
/// so the same dBm value represents different signal quality:
/// - 2.4 GHz: high noise floor (~-85 dBm), needs stronger signal for usable SNR
/// - 5 GHz: moderate noise floor (~-92 dBm)
/// - 6 GHz: very low noise floor (~-95 to -100 dBm), good rates even at weaker signal
/// </summary>
public static class SignalClassification
{
    /// <summary>
    /// Get the CSS class for signal strength, accounting for band-specific noise floors.
    /// Returns "signal-excellent", "signal-good", "signal-fair", "signal-weak", or "signal-poor".
    /// </summary>
    public static string GetSignalClass(int dbm, RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => dbm switch
        {
            >= -50 => "signal-excellent",
            >= -60 => "signal-good",
            >= -67 => "signal-fair",
            >= -75 => "signal-weak",
            _ => "signal-poor"
        },
        RadioBand.Band6GHz => dbm switch
        {
            >= -67 => "signal-excellent",
            >= -78 => "signal-good",
            >= -87 => "signal-fair",
            >= -92 => "signal-weak",
            _ => "signal-poor"
        },
        // 5 GHz and unknown/default
        _ => dbm switch
        {
            >= -60 => "signal-excellent",
            >= -70 => "signal-good",
            >= -78 => "signal-fair",
            >= -85 => "signal-weak",
            _ => "signal-poor"
        }
    };

    /// <summary>
    /// Overload accepting the UniFi radio band string (ng, na, 6e).
    /// </summary>
    public static string GetSignalClass(int dbm, string? bandString) =>
        GetSignalClass(dbm, ParseBand(bandString));

    /// <summary>
    /// Get signal class for a nullable signal value. Returns empty string if null.
    /// </summary>
    public static string GetSignalClass(int? dbm, RadioBand band) =>
        dbm.HasValue ? GetSignalClass(dbm.Value, band) : "";

    /// <summary>
    /// Get signal class for a nullable signal value with band string.
    /// </summary>
    public static string GetSignalClass(int? dbm, string? bandString) =>
        dbm.HasValue ? GetSignalClass(dbm.Value, ParseBand(bandString)) : "";

    /// <summary>
    /// Returns true if the signal is considered "weak" or "poor" for the given band.
    /// Used by health rules and scoring to determine if a client has problematic signal.
    /// </summary>
    public static bool IsWeakSignal(int dbm, RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => dbm < -67,
        RadioBand.Band6GHz => dbm < -87,
        _ => dbm < -78 // 5 GHz default
    };

    /// <summary>
    /// Returns true if the signal is critically weak (poor) for the given band.
    /// </summary>
    public static bool IsCriticalSignal(int dbm, RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => dbm < -75,
        RadioBand.Band6GHz => dbm < -92,
        _ => dbm < -85 // 5 GHz default
    };

    /// <summary>
    /// Get the weak signal threshold for a band (dBm value below which signal is "weak").
    /// </summary>
    public static int GetWeakThreshold(RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => -67,
        RadioBand.Band6GHz => -87,
        _ => -78
    };

    /// <summary>
    /// Get the number of signal bars (1-5) for a given signal class.
    /// </summary>
    public static int GetBarCount(string signalClass) => signalClass switch
    {
        "signal-excellent" => 5,
        "signal-good" => 4,
        "signal-fair" => 3,
        "signal-weak" => 2,
        _ => 1
    };

    private static RadioBand ParseBand(string? bandString) => bandString switch
    {
        "ng" => RadioBand.Band2_4GHz,
        "6e" => RadioBand.Band6GHz,
        "na" => RadioBand.Band5GHz,
        _ => RadioBand.Band5GHz
    };
}
