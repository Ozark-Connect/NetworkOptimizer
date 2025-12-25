namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Result of analyzing a speed test against the network path.
/// Combines path information with performance grading.
/// </summary>
public class PathAnalysisResult
{
    /// <summary>The network path from iperf3 server to target device</summary>
    public NetworkPath Path { get; set; } = new();

    /// <summary>Measured throughput from device to server (Mbps)</summary>
    public double MeasuredFromDeviceMbps { get; set; }

    /// <summary>Measured throughput to device from server (Mbps)</summary>
    public double MeasuredToDeviceMbps { get; set; }

    /// <summary>Efficiency of from-device transfer vs theoretical max (%)</summary>
    public double FromDeviceEfficiencyPercent { get; set; }

    /// <summary>Efficiency of to-device transfer vs theoretical max (%)</summary>
    public double ToDeviceEfficiencyPercent { get; set; }

    /// <summary>Performance grade for from-device transfer</summary>
    public PerformanceGrade FromDeviceGrade { get; set; }

    /// <summary>Performance grade for to-device transfer</summary>
    public PerformanceGrade ToDeviceGrade { get; set; }

    /// <summary>Observations about the test results</summary>
    public List<string> Insights { get; set; } = new();

    /// <summary>Suggestions for improving performance</summary>
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Calculate efficiency and grade based on measured vs theoretical speeds
    /// </summary>
    public void CalculateEfficiency()
    {
        if (Path.RealisticMaxMbps > 0)
        {
            FromDeviceEfficiencyPercent = (MeasuredFromDeviceMbps / Path.RealisticMaxMbps) * 100;
            ToDeviceEfficiencyPercent = (MeasuredToDeviceMbps / Path.RealisticMaxMbps) * 100;

            FromDeviceGrade = GetGrade(FromDeviceEfficiencyPercent);
            ToDeviceGrade = GetGrade(ToDeviceEfficiencyPercent);
        }
    }

    private static PerformanceGrade GetGrade(double efficiencyPercent) => efficiencyPercent switch
    {
        >= 90 => PerformanceGrade.Excellent,
        >= 75 => PerformanceGrade.Good,
        >= 50 => PerformanceGrade.Fair,
        >= 25 => PerformanceGrade.Poor,
        _ => PerformanceGrade.Critical
    };

    /// <summary>
    /// Generate insights based on the analysis.
    /// Note: Path info (routing, bottleneck) shown separately in UI - don't duplicate here.
    /// </summary>
    public void GenerateInsights()
    {
        Insights.Clear();
        Recommendations.Clear();

        // Gateway tests have inherent CPU overhead - note this and skip performance warnings
        if (Path.TargetIsGateway)
        {
            Insights.Add("Gateway speed test - results limited by gateway CPU, not network");
            // Skip other performance-based insights for gateway tests
            return;
        }

        // AP tests are CPU-limited; anything above ~4.4 Gbps is considered good
        const double ApGoodSpeedThreshold = 4400;
        bool apPerformingWell = Path.TargetIsAccessPoint &&
            (MeasuredFromDeviceMbps >= ApGoodSpeedThreshold || MeasuredToDeviceMbps >= ApGoodSpeedThreshold);

        if (Path.TargetIsAccessPoint && apPerformingWell)
        {
            Insights.Add("AP speed test - results limited by AP CPU, not network");
            return;
        }

        // Wireless connection warning (client->AP or AP->AP, not AP->Switch)
        if (Path.HasWirelessConnection)
        {
            Insights.Add("Path includes wireless segment - speeds may vary with signal quality");
        }

        // Performance-based insights (note: enum comparison - higher value = worse grade)
        var avgEfficiency = (FromDeviceEfficiencyPercent + ToDeviceEfficiencyPercent) / 2;

        if (FromDeviceGrade >= PerformanceGrade.Poor || ToDeviceGrade >= PerformanceGrade.Poor)
        {
            Insights.Add("Performance below expected - possible congestion or network issue");

            if (Math.Abs(FromDeviceEfficiencyPercent - ToDeviceEfficiencyPercent) > 20)
            {
                Recommendations.Add("Large asymmetry detected - check for half-duplex links or congestion");
            }
        }
        else if (FromDeviceGrade == PerformanceGrade.Fair || ToDeviceGrade == PerformanceGrade.Fair)
        {
            Insights.Add("Performance is moderate - some overhead or minor congestion");
        }

        // Recommendations based on bottleneck
        if (Path.TheoreticalMaxMbps <= 100)
        {
            Recommendations.Add("100 Mbps link detected - consider upgrading to gigabit");
        }
        else if (Path.TheoreticalMaxMbps == 1000 && avgEfficiency >= 90)
        {
            Recommendations.Add("Maxing out 1 GbE - consider 2.5G or 10G upgrade for higher speeds");
        }
    }
}

/// <summary>
/// Performance grade based on efficiency percentage
/// </summary>
public enum PerformanceGrade
{
    /// <summary>90%+ of theoretical maximum</summary>
    Excellent,

    /// <summary>75-89% of theoretical maximum</summary>
    Good,

    /// <summary>50-74% of theoretical maximum</summary>
    Fair,

    /// <summary>25-49% of theoretical maximum</summary>
    Poor,

    /// <summary>Under 25% of theoretical maximum</summary>
    Critical
}
