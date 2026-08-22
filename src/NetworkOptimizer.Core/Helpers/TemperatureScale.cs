namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Normalizes device temperature readings to degrees Celsius.
/// </summary>
public static class TemperatureScale
{
    /// <summary>Above this a reading is millidegrees, not degrees.</summary>
    private const double PlausibleMaxCelsius = 200.0;

    /// <summary>
    /// Returns <paramref name="value"/> in degrees Celsius, scaling it down when the device
    /// reported millidegrees. Scaling varies by model rather than by source - the UXG-Lite
    /// reports its raw thermal-zone value over the UniFi API where other gateways report
    /// degrees - so read the scale off the value instead of assuming one.
    /// </summary>
    public static double NormalizeCelsius(double value) =>
        Math.Abs(value) > PlausibleMaxCelsius ? value / 1000.0 : value;

    /// <summary>Null-tolerant <see cref="NormalizeCelsius(double)"/>.</summary>
    public static double? NormalizeCelsius(double? value) =>
        value.HasValue ? NormalizeCelsius(value.Value) : null;
}
