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

    /// <summary>TCP retransmits from device to server</summary>
    public int FromDeviceRetransmits { get; set; }

    /// <summary>TCP retransmits to device from server</summary>
    public int ToDeviceRetransmits { get; set; }

    /// <summary>Bytes transferred from device to server</summary>
    public long FromDeviceBytes { get; set; }

    /// <summary>Bytes transferred to device from server</summary>
    public long ToDeviceBytes { get; set; }

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
        // But not for external (VPN/WAN) paths where the target isn't really the gateway
        if (Path.TargetIsGateway && !Path.IsExternalPath)
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

        // Recommendations based on bottleneck (wired only - wireless speeds vary naturally)
        // 10/100 Mbps links on UniFi gear typically indicate cable or auto-negotiation issues
        if ((Path.TheoreticalMaxMbps == 10 || Path.TheoreticalMaxMbps == 100) && !Path.HasWirelessConnection)
        {
            Recommendations.Add("10/100 Mbps link detected - cable quality or auto-negotiation may be faulty");
        }
        else if (Path.TheoreticalMaxMbps == 1000 && avgEfficiency >= 90)
        {
            Recommendations.Add("Maxing out 1 GbE - consider 2.5G or 10G upgrade for higher speeds");
        }

        // Retransmit analysis
        AnalyzeRetransmits();
    }

    /// <summary>
    /// Analyze TCP retransmits and generate insights about packet loss.
    /// Uses percentage-based thresholds: 0.1% is concerning, with higher thresholds for UniFi devices.
    /// </summary>
    private void AnalyzeRetransmits()
    {
        // Skip if no retransmits
        if (FromDeviceRetransmits == 0 && ToDeviceRetransmits == 0)
            return;

        // Calculate retransmit percentages based on estimated packet counts
        // TCP MSS is typically ~1460 bytes, but we use 1500 for simplicity
        const int EstimatedPacketSize = 1500;

        var fromDevicePackets = FromDeviceBytes > 0 ? FromDeviceBytes / EstimatedPacketSize : 0;
        var toDevicePackets = ToDeviceBytes > 0 ? ToDeviceBytes / EstimatedPacketSize : 0;

        var fromDeviceRetransmitPercent = fromDevicePackets > 0
            ? (FromDeviceRetransmits * 100.0 / fromDevicePackets)
            : 0;
        var toDeviceRetransmitPercent = toDevicePackets > 0
            ? (ToDeviceRetransmits * 100.0 / toDevicePackets)
            : 0;

        // UniFi devices (APs, gateways, cellular modems) are CPU-bound and may show higher retransmits
        // Use higher thresholds for UniFi devices: 1% elevated, 2% high
        // Regular clients: 0.6% elevated, 1.2% high
        var isUniFiDevice = Path.TargetIsAccessPoint || Path.TargetIsGateway || Path.TargetIsCellularModem;
        var highThresholdPercent = isUniFiDevice ? 1.0 : 0.6;
        var veryHighThresholdPercent = isUniFiDevice ? 2.0 : 1.2;

        // Determine if this is a wireless client (not an AP but has wireless connection)
        var isWirelessClient = Path.HasWirelessConnection && !Path.TargetIsAccessPoint;
        var isMeshedAp = Path.TargetIsAccessPoint && Path.HasWirelessConnection;

        // Analyze to-device direction (data flowing to the test device)
        if (ToDeviceRetransmits > 0 && toDeviceRetransmitPercent >= highThresholdPercent)
        {
            var severity = toDeviceRetransmitPercent >= veryHighThresholdPercent ? "High" : "Elevated";
            Insights.Add($"{severity} packet loss to device ({ToDeviceRetransmits:N0} retransmits, {toDeviceRetransmitPercent:F2}%)");

            if (isWirelessClient)
            {
                Recommendations.Add("Retransmits to device on Wi-Fi - check signal strength and interference");
            }
            else if (isMeshedAp)
            {
                Recommendations.Add("Retransmits to device on wireless mesh - check mesh backhaul signal quality");
            }
        }

        // Analyze from-device direction (data flowing from the test device)
        if (FromDeviceRetransmits > 0 && fromDeviceRetransmitPercent >= highThresholdPercent)
        {
            var severity = fromDeviceRetransmitPercent >= veryHighThresholdPercent ? "High" : "Elevated";
            Insights.Add($"{severity} packet loss from device ({FromDeviceRetransmits:N0} retransmits, {fromDeviceRetransmitPercent:F2}%)");

            if (isWirelessClient)
            {
                Recommendations.Add("Retransmits from device on Wi-Fi - client may have weak signal or interference");
            }
            else if (isMeshedAp)
            {
                Recommendations.Add("Retransmits from device on wireless mesh - may indicate mesh uplink contention");
            }
        }

        // If both directions have issues, add general recommendation
        if (fromDeviceRetransmitPercent >= highThresholdPercent && toDeviceRetransmitPercent >= highThresholdPercent)
        {
            if (!Path.HasWirelessConnection)
            {
                Recommendations.Add("Bidirectional packet loss - check for network congestion or faulty cables");
            }
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
