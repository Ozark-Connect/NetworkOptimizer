namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// The live stat tiles' number formats, in one place because the Monitoring page and the shared
/// Live View panel render the same tiles and had a private copy each.
/// <para>
/// Fixed decimals rather than trimmed ones. These figures update every few seconds, and a width
/// that changes with the value makes a column of them twitch - "9.9" to "10" moves everything
/// beside it. Holding the decimals steady costs a character and buys a number you can read while
/// it changes, which is the whole point of a live tile.
/// </para>
/// </summary>
public static class MonitoringStatFormat
{
    /// <summary>Round-trip time as "1.00 ms", or "-" when nothing has been measured.</summary>
    public static string Rtt(double? ms) => ms.HasValue ? $"{ms.Value:0.00} ms" : "-";

    /// <summary>Loss as "0.0%". Zero is shown to the same precision - it is a reading, not an absence.</summary>
    public static string Loss(double percent) => $"{percent:0.0}%";
}
