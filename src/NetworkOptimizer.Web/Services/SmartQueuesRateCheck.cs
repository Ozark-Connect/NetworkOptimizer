namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Compares a WAN's Smart Queues rates in UniFi Network with the highest rates Adaptive SQM shapes at.
/// Adaptive SQM sets the shaping rate itself, so a lower UniFi rate does not block a deploy, but it
/// still matters: UniFi's QoS-rule classes keep their ceiling at UniFi's rate, and UniFi's own tree
/// shapes until the first calibration and after any rebuild.
/// </summary>
public static class SmartQueuesRateCheck
{
    /// <summary>Headroom the ping script leaves below a known physical link speed (LINK_SPEED_HEADROOM).</summary>
    private const double LinkSpeedHeadroom = 0.98;

    /// <summary>
    /// The highest download rate the ping script can set: its configured maximum, capped below the
    /// physical link speed when that is known.
    /// </summary>
    public static int MaxShapedDownloadMbps(int maxDownloadMbps, int? linkSpeedMbps) =>
        linkSpeedMbps is > 0 ? Math.Min(maxDownloadMbps, (int)(linkSpeedMbps.Value * LinkSpeedHeadroom)) : maxDownloadMbps;

    /// <summary>
    /// A warning for each direction where UniFi's Smart Queues rate is below Adaptive SQM's highest
    /// rate. Equal is fine: a QoS class whose ceiling matches the root's loses nothing. A direction
    /// UniFi reports no rate for is skipped.
    /// </summary>
    public static List<string> Warnings(string wanName, int? unifiDownloadMbps, int? unifiUploadMbps,
        int maxShapedDownloadMbps, int maxShapedUploadMbps)
    {
        var warnings = new List<string>();
        if (unifiDownloadMbps is > 0 and var down && down < maxShapedDownloadMbps)
            warnings.Add(Warning(wanName, "download", down, maxShapedDownloadMbps));
        if (unifiUploadMbps is > 0 and var up && up < maxShapedUploadMbps)
            warnings.Add(Warning(wanName, "upload", up, maxShapedUploadMbps));
        return warnings;
    }

    private static string Warning(string wanName, string direction, int unifiMbps, int adaptiveMbps) =>
        $"{wanName}: UniFi Network's Smart Queues {direction} rate ({unifiMbps} Mbps) is below Adaptive SQM's " +
        $"{adaptiveMbps} Mbps. QoS-rule traffic stays at {unifiMbps} Mbps, as does all {direction} until the first " +
        $"calibration. Set it to {adaptiveMbps} Mbps or more.";
}
