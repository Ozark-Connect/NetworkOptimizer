using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>
/// Band-aware signal strength classification. Different bands have different noise floors,
/// so the same dBm value represents different signal quality:
/// - 2.4 GHz: high noise floor (~-85 dBm), but better wall penetration and range
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
            >= -55 => "signal-excellent",
            >= -65 => "signal-good",
            >= -73 => "signal-fair",
            >= -80 => "signal-weak",
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
        RadioBand.Band2_4GHz => dbm < -73,
        RadioBand.Band6GHz => dbm < -87,
        _ => dbm < -78 // 5 GHz default
    };

    /// <summary>
    /// Returns true if the signal is critically weak (poor) for the given band.
    /// </summary>
    public static bool IsCriticalSignal(int dbm, RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => dbm < -80,
        RadioBand.Band6GHz => dbm < -92,
        _ => dbm < -85 // 5 GHz default
    };

    /// <summary>
    /// Get the weak signal threshold for a band (dBm value below which signal is "weak").
    /// </summary>
    public static int GetWeakThreshold(RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => -73,
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

    // Both the UniFi radio codes and the normalized forms other surfaces carry. A band that falls
    // through here is classified on the 5 GHz curve, so a missing case is a silently wrong colour.

    /// <summary>
    /// How many of five bars are lit, 0-5. A different curve from <see cref="GetSignalClass"/>:
    /// the class colours the bars, this fills them, and the two disagree by design.
    /// </summary>
    public static int GetSignalBars(int dbm, RadioBand band)
    {
        var thresholds = band switch
        {
            RadioBand.Band2_4GHz => new[] { -82, -75, -67, -60, -50 },
            RadioBand.Band6GHz => new[] { -97, -92, -87, -78, -67 },
            _ => new[] { -92, -85, -78, -70, -60 }
        };

        var bars = 0;
        foreach (var t in thresholds)
        {
            if (dbm >= t) bars++;
        }
        return bars;
    }

    /// <summary>Bars for a band string, in either the UniFi or normalized form.</summary>
    public static int GetSignalBars(int dbm, string? bandString) => GetSignalBars(dbm, ParseBand(bandString));

    /// <summary>Per-band offset from the 5 GHz curve, for continuous scales that must agree with it.</summary>
    public static int BandOffsetDb(RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => 5,
        RadioBand.Band6GHz => -8,
        _ => 0
    };

    /// <summary>Per-band offset for a band string, in either form.</summary>
    public static int BandOffsetDb(string? bandString) => BandOffsetDb(ParseBand(bandString));

    private static RadioBand ParseBand(string? bandString) => bandString switch
    {
        "ng" or "2.4" or "2.4ghz" or "2.4 GHz" => RadioBand.Band2_4GHz,
        "6e" or "6" or "6ghz" or "6 GHz" => RadioBand.Band6GHz,
        "na" or "5" or "5ghz" or "5 GHz" => RadioBand.Band5GHz,
        _ => RadioBand.Band5GHz
    };
}
