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
    /// Generate insights based on the analysis
    /// </summary>
    public void GenerateInsights()
    {
        Insights.Clear();
        Recommendations.Clear();

        // Path information
        if (Path.RequiresRouting)
        {
            Insights.Add($"Traffic is routed through {Path.GatewayDevice ?? "gateway"} (inter-VLAN)");
        }

        if (Path.HasWirelessSegment)
        {
            Insights.Add("Path includes wireless segment - speeds may vary with signal quality");
        }

        if (Path.SwitchHopCount > 0)
        {
            Insights.Add($"Path traverses {Path.SwitchHopCount} switch hop{(Path.SwitchHopCount > 1 ? "s" : "")}");
        }

        // Bottleneck
        if (!string.IsNullOrEmpty(Path.BottleneckDescription))
        {
            Insights.Add($"Bottleneck: {Path.BottleneckDescription}");
        }

        // Performance insights
        if (FromDeviceGrade == PerformanceGrade.Excellent && ToDeviceGrade == PerformanceGrade.Excellent)
        {
            Insights.Add("Performance is excellent - achieving near-theoretical maximum");
        }
        else if (FromDeviceGrade <= PerformanceGrade.Poor || ToDeviceGrade <= PerformanceGrade.Poor)
        {
            Insights.Add("Performance is below expected - possible network issue or congestion");

            if (Math.Abs(FromDeviceEfficiencyPercent - ToDeviceEfficiencyPercent) > 20)
            {
                Recommendations.Add("Large asymmetry detected - check for half-duplex links or congestion");
            }
        }

        // Recommendations based on bottleneck
        if (Path.TheoreticalMaxMbps <= 100 && Path.SwitchHopCount > 0)
        {
            Recommendations.Add("Consider upgrading to gigabit links - 100M port detected in path");
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
