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

    /// <summary>
    /// How many of five bars are lit, 1-5, derived from the class so the two can never disagree.
    /// Never give the count its own thresholds: boundaries that do not line up draw the same bar
    /// count in two different class colors.
    /// </summary>
    public static int GetSignalBars(int dbm, RadioBand band) => GetBarCount(GetSignalClass(dbm, band));

    /// <summary>Bars for a band string, in either the UniFi or normalized form.</summary>
    public static int GetSignalBars(int dbm, string? bandString) => GetSignalBars(dbm, ParseBand(bandString));

    /// <summary>
    /// The color ramp for a band, as (dBm, hex) from strongest to weakest. Anchored to the same
    /// thresholds <see cref="GetSignalClass"/> uses and painted in the class colors, so a reading
    /// at a boundary is exactly its badge color and anything between blends its neighbors. One
    /// curve for gauges, dots and heat surfaces alike - a second scale is how they drift apart.
    /// </summary>
    public static (int Dbm, string Hex)[] GetSignalGradient(RadioBand band)
    {
        var (excellent, good, fair, weak) = band switch
        {
            RadioBand.Band2_4GHz => (-55, -65, -73, -80),
            RadioBand.Band6GHz => (-67, -78, -87, -92),
            _ => (-60, -70, -78, -85)
        };

        // The class boundaries are the anchors, so a reading at one is exactly its badge color, and
        // the endpoints sit a fixed offset past them rather than at a fixed dBm: an absolute top
        // stop would span 25 dB on 2.4 GHz and 37 on 6 GHz, which reads as two different scales.
        //
        // The top reaches 25 dB above excellent because a client beside its access point sits far
        // above that boundary on every band, and anything past the last stop is one flat color.
        // Hue travels the whole way - cyan, teal, emerald, green, lime, yellow, orange, rose, dark
        // red - since most readings sit in the strong half. The three mid-span stops are the only
        // ones off a boundary.
        var excellentToGood = (excellent + good) / 2;
        var fairToWeak = (fair + weak) / 2;

        return
        [
            (excellent + 25, "#a5f3fc"),
            (excellent + 15, "#22d3ee"),
            (excellent + 7, "#2dd4bf"),
            (excellent, "#10b981"),
            (excellentToGood, "#4ade80"),
            (good, "#84cc16"),
            (fair, "#fde047"),
            (fairToWeak, "#fb923c"),
            (weak, "#f43f5e"),
            (weak - 10, "#991b1b")
        ];
    }

    /// <summary>The ramp for a band string, in either the UniFi or normalized form.</summary>
    public static (int Dbm, string Hex)[] GetSignalGradient(string? bandString) => GetSignalGradient(ParseBand(bandString));

    /// <summary>
    /// The blended color for a reading, as "rgb(r,g,b)". Every surface that displays a dBm uses
    /// this so the value reads continuously: a decibel of change moves the color a little, rather
    /// than snapping a whole class at a threshold. The class is still what decides a verdict -
    /// bar counts, scoring, distribution counts - but it is not what paints a number.
    /// </summary>
    public static string GetSignalColor(int dbm, RadioBand band)
    {
        var stops = GetSignalGradient(band);
        if (dbm >= stops[0].Dbm) return Rgb(stops[0].Hex);
        if (dbm <= stops[^1].Dbm) return Rgb(stops[^1].Hex);

        for (var i = 0; i < stops.Length - 1; i++)
        {
            if (dbm > stops[i].Dbm || dbm < stops[i + 1].Dbm) continue;
            var t = (double)(dbm - stops[i + 1].Dbm) / (stops[i].Dbm - stops[i + 1].Dbm);
            var (ar, ag, ab) = Rgb3(stops[i].Hex);
            var (br, bg, bb) = Rgb3(stops[i + 1].Hex);
            return $"rgb({(int)(ar * t + br * (1 - t))},{(int)(ag * t + bg * (1 - t))},{(int)(ab * t + bb * (1 - t))})";
        }
        return Rgb(stops[^1].Hex);
    }

    /// <summary>The blended color for a band string, in either form.</summary>
    public static string GetSignalColor(int dbm, string? bandString) => GetSignalColor(dbm, ParseBand(bandString));

    /// <summary>The blended color for a nullable reading, or empty when there is none.</summary>
    public static string GetSignalColor(int? dbm, string? bandString) =>
        dbm.HasValue ? GetSignalColor(dbm.Value, ParseBand(bandString)) : "";

    /// <inheritdoc cref="GetSignalColor(int?, string?)"/>
    public static string GetSignalColor(int? dbm, RadioBand band) =>
        dbm.HasValue ? GetSignalColor(dbm.Value, band) : "";

    private static (int R, int G, int B) Rgb3(string hex) => (
        Convert.ToInt32(hex.Substring(1, 2), 16),
        Convert.ToInt32(hex.Substring(3, 2), 16),
        Convert.ToInt32(hex.Substring(5, 2), 16));

    private static string Rgb(string hex)
    {
        var (r, g, b) = Rgb3(hex);
        return $"rgb({r},{g},{b})";
    }

    // Both the UniFi radio codes and the normalized forms other surfaces carry. A band that falls
    // through here is classified on the 5 GHz curve, so a missing case is a silently wrong color.
    private static RadioBand ParseBand(string? bandString) => bandString switch
    {
        "ng" or "2.4" or "2.4ghz" or "2.4 GHz" => RadioBand.Band2_4GHz,
        "6e" or "6" or "6ghz" or "6 GHz" => RadioBand.Band6GHz,
        "na" or "5" or "5ghz" or "5 GHz" => RadioBand.Band5GHz,
        _ => RadioBand.Band5GHz
    };
}
