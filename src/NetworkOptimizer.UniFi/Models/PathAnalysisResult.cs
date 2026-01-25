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
    /// Overhead factors for different link types
    /// </summary>
    public const double ClientWifiOverheadFactor = 0.75;    // 25% overhead
    public const double MeshBackhaulOverheadFactor = 0.45;  // 55% overhead
    public const double WiredOverheadFactor = 0.94;         // 6% overhead
    public const double WanOverheadFactor = 0.94;           // 6% overhead

    /// <summary>
    /// Get the overhead factor for this path based on the bottleneck link type.
    /// Only uses mesh overhead (55%) if mesh backhaul is actually the bottleneck.
    /// </summary>
    public double GetOverheadFactor()
    {
        if (!Path.HasWirelessConnection)
            return WiredOverheadFactor;

        // Find mesh hop if present
        var meshHop = Path.Hops.FirstOrDefault(h =>
            h.IngressPortName?.Contains("mesh", StringComparison.OrdinalIgnoreCase) == true ||
            h.EgressPortName?.Contains("mesh", StringComparison.OrdinalIgnoreCase) == true);

        if (meshHop != null)
        {
            // Get mesh backhaul speed
            var meshSpeedMbps = meshHop.IngressSpeedMbps > 0 && meshHop.EgressSpeedMbps > 0
                ? Math.Min(meshHop.IngressSpeedMbps, meshHop.EgressSpeedMbps)
                : Math.Max(meshHop.IngressSpeedMbps, meshHop.EgressSpeedMbps);

            // Mesh overhead only if mesh is the bottleneck (matches TheoreticalMaxMbps)
            // If path max is lower than mesh, something else (client Wi-Fi) is the bottleneck
            if (meshSpeedMbps > 0 && meshSpeedMbps <= Path.TheoreticalMaxMbps)
            {
                return MeshBackhaulOverheadFactor;
            }
        }

        return ClientWifiOverheadFactor;
    }

    /// <summary>
    /// Get the overhead percentage for display (e.g., "25%" for client Wi-Fi)
    /// </summary>
    public int GetOverheadPercent()
    {
        var factor = GetOverheadFactor();
        return (int)Math.Round((1 - factor) * 100);
    }

    /// <summary>
    /// Calculate directional efficiency at display time using stored Wi-Fi TX/RX rates.
    /// This provides accurate efficiency for asymmetric links (where TX ≠ RX).
    ///
    /// Direction mapping (critical - do not change):
    /// - FromDevice (↓): Client SENDS → AP RECEIVES → uses RX rate (WifiRxRateKbps)
    /// - ToDevice (↑): Server SENDS → AP TRANSMITS → uses TX rate (WifiTxRateKbps)
    /// </summary>
    /// <param name="wifiRxRateKbps">AP RX rate in Kbps (limits FromDevice direction)</param>
    /// <param name="wifiTxRateKbps">AP TX rate in Kbps (limits ToDevice direction)</param>
    /// <returns>Tuple of (fromDeviceMaxMbps, toDeviceMaxMbps, fromEfficiency%, toEfficiency%, overheadPercent)</returns>
    public (double fromDeviceMaxMbps, double toDeviceMaxMbps, double fromEfficiency, double toEfficiency, int overheadPercent)
        GetDirectionalEfficiency(long? wifiRxRateKbps, long? wifiTxRateKbps)
    {
        // Use stored directional rates if available (wireless clients with TX/RX data, or WAN/VPN)
        if (wifiRxRateKbps.HasValue && wifiRxRateKbps.Value > 0 &&
            wifiTxRateKbps.HasValue && wifiTxRateKbps.Value > 0)
        {
            // Determine overhead based on path type
            double overheadFactor;
            if (Path.IsExternalPath)
            {
                // WAN/VPN paths use wired overhead (6%)
                overheadFactor = WanOverheadFactor;
            }
            else
            {
                // Wi-Fi paths: check if mesh backhaul is the bottleneck
                // Only use mesh overhead (55%) if mesh is slower than client Wi-Fi link
                var meshHop = Path.Hops.FirstOrDefault(h =>
                    h.IngressPortName?.Contains("mesh", StringComparison.OrdinalIgnoreCase) == true ||
                    h.EgressPortName?.Contains("mesh", StringComparison.OrdinalIgnoreCase) == true);

                if (meshHop != null)
                {
                    // Get mesh backhaul speed (use min of ingress/egress, or whichever is set)
                    var meshSpeedMbps = meshHop.IngressSpeedMbps > 0 && meshHop.EgressSpeedMbps > 0
                        ? Math.Min(meshHop.IngressSpeedMbps, meshHop.EgressSpeedMbps)
                        : Math.Max(meshHop.IngressSpeedMbps, meshHop.EgressSpeedMbps);

                    // Get client Wi-Fi speed (use min of RX/TX)
                    var clientSpeedMbps = Math.Min(wifiRxRateKbps.Value, wifiTxRateKbps.Value) / 1000.0;

                    // Mesh overhead only if mesh is the bottleneck
                    overheadFactor = meshSpeedMbps < clientSpeedMbps
                        ? MeshBackhaulOverheadFactor
                        : ClientWifiOverheadFactor;
                }
                else
                {
                    overheadFactor = ClientWifiOverheadFactor;
                }
            }
            var overheadPercent = (int)Math.Round((1 - overheadFactor) * 100);

            // RX = AP receives from client = FromDevice direction limit
            // TX = AP transmits to client = ToDevice direction limit
            var fromDeviceMaxMbps = wifiRxRateKbps.Value / 1000.0;
            var toDeviceMaxMbps = wifiTxRateKbps.Value / 1000.0;
            var fromRealistic = fromDeviceMaxMbps * overheadFactor;
            var toRealistic = toDeviceMaxMbps * overheadFactor;

            var fromEfficiency = fromRealistic > 0 ? (MeasuredFromDeviceMbps / fromRealistic) * 100 : 0;
            var toEfficiency = toRealistic > 0 ? (MeasuredToDeviceMbps / toRealistic) * 100 : 0;

            return (fromDeviceMaxMbps, toDeviceMaxMbps, fromEfficiency, toEfficiency, overheadPercent);
        }

        // Fall back to symmetric calculation (legacy results or wired clients)
        var fallbackOverheadPercent = GetOverheadPercent();
        return (
            Path.TheoreticalMaxMbps,
            Path.TheoreticalMaxMbps,
            FromDeviceEfficiencyPercent,
            ToDeviceEfficiencyPercent,
            fallbackOverheadPercent
        );
    }

    /// <summary>
    /// Check if the link is asymmetric (>10% difference between TX and RX rates)
    /// </summary>
    public static bool IsAsymmetric(long? wifiRxRateKbps, long? wifiTxRateKbps)
    {
        if (!wifiRxRateKbps.HasValue || !wifiTxRateKbps.HasValue ||
            wifiRxRateKbps.Value <= 0 || wifiTxRateKbps.Value <= 0)
            return false;

        var maxRate = Math.Max(wifiRxRateKbps.Value, wifiTxRateKbps.Value);
        var minRate = Math.Min(wifiRxRateKbps.Value, wifiTxRateKbps.Value);
        var difference = (maxRate - minRate) / (double)maxRate;

        return difference > 0.10; // More than 10% difference
    }

    /// <summary>
    /// Extract directional rates from path data (mesh hops or WAN).
    /// Used to populate Iperf3Result.WifiTxRateKbps/WifiRxRateKbps for asymmetric display.
    /// Returns null if no directional rates are available in the path.
    ///
    /// Direction mapping for mesh APs:
    /// - TX (FromDevice ↓): Mesh AP transmits toward uplink/server
    /// - RX (ToDevice ↑): Mesh AP receives from uplink/server
    ///
    /// Direction mapping for WAN:
    /// - Ingress/Download (FromDevice ↓): Data from external toward server
    /// - Egress/Upload (ToDevice ↑): Data from server toward external
    /// </summary>
    public (long? rxKbps, long? txKbps) GetDirectionalRatesFromPath()
    {
        // For mesh APs: get rates from the wireless hop
        // Note: Mesh direction is opposite of client Wi-Fi because we're looking at
        // the mesh AP's perspective transmitting TO (not receiving FROM) the server direction
        if (Path.TargetIsAccessPoint && Path.HasWirelessConnection)
        {
            var wirelessHop = Path.Hops.FirstOrDefault(h =>
                h.WirelessTxRateMbps.HasValue && h.WirelessRxRateMbps.HasValue &&
                h.WirelessTxRateMbps.Value > 0 && h.WirelessRxRateMbps.Value > 0);

            if (wirelessHop != null)
            {
                // Mesh: TX limits FromDevice (AP transmits toward server), RX limits ToDevice (AP receives from server)
                return (wirelessHop.WirelessTxRateMbps!.Value * 1000L, wirelessHop.WirelessRxRateMbps!.Value * 1000L);
            }
        }

        // For WAN/VPN: use hop ingress/egress speeds if asymmetric
        // These are set during path tracing from WAN provider capabilities
        if (Path.IsExternalPath)
        {
            var externalHop = Path.Hops.FirstOrDefault(h =>
                h.Type == HopType.Wan || h.Type == HopType.Vpn ||
                h.Type == HopType.Teleport || h.Type == HopType.Tailscale);

            if (externalHop != null &&
                externalHop.IngressSpeedMbps > 0 && externalHop.EgressSpeedMbps > 0 &&
                externalHop.IngressSpeedMbps != externalHop.EgressSpeedMbps)
            {
                // Ingress = download (FromDevice), Egress = upload (ToDevice)
                return (externalHop.IngressSpeedMbps * 1000L, externalHop.EgressSpeedMbps * 1000L);
            }
        }

        return (null, null);
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
