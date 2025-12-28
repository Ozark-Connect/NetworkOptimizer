namespace NetworkOptimizer.Sqm.Models;

/// <summary>
/// Connection type for SQM profile selection
/// </summary>
public enum ConnectionType
{
    /// <summary>DOCSIS Cable (Coax) - Stable speeds with peak-hour congestion</summary>
    DocsisCable,

    /// <summary>Starlink Satellite - Variable speeds, weather-sensitive, higher latency</summary>
    Starlink,

    /// <summary>Fiber (FTTH/FTTP) - Very stable, low latency, high speed</summary>
    Fiber,

    /// <summary>DSL (ADSL/VDSL) - Stable, lower speeds, distance-dependent</summary>
    Dsl,

    /// <summary>Fixed Wireless (WISP) - Variable, weather-sensitive</summary>
    FixedWireless,

    /// <summary>Fixed LTE/5G - Variable, cell congestion-sensitive</summary>
    CellularHome
}

/// <summary>
/// Connection profile with intelligent speed assumptions based on connection type
/// </summary>
public class ConnectionProfile
{
    /// <summary>
    /// Type of internet connection
    /// </summary>
    public ConnectionType Type { get; set; }

    /// <summary>
    /// Friendly name for the connection (e.g., "Yelcot", "Starlink")
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// WAN interface name (e.g., "eth2", "eth0")
    /// </summary>
    public string Interface { get; set; } = "eth2";

    /// <summary>
    /// IFB (Intermediate Functional Block) device for traffic shaping
    /// </summary>
    public string IfbDevice => $"ifb{Interface}";

    /// <summary>
    /// Advertised/nominal download speed in Mbps (what customer pays for)
    /// </summary>
    public int NominalDownloadMbps { get; set; }

    /// <summary>
    /// Advertised/nominal upload speed in Mbps
    /// </summary>
    public int NominalUploadMbps { get; set; }

    /// <summary>
    /// Calculated maximum download speed (ceiling) based on connection type
    /// </summary>
    public int MaxDownloadMbps => CalculateMaxSpeed(NominalDownloadMbps);

    /// <summary>
    /// Calculated minimum download speed (floor) based on connection type
    /// </summary>
    public int MinDownloadMbps => CalculateMinSpeed(NominalDownloadMbps);

    /// <summary>
    /// Absolute maximum achievable speed (used for rate limiting)
    /// </summary>
    public int AbsoluteMaxDownloadMbps => CalculateAbsoluteMax(NominalDownloadMbps);

    /// <summary>
    /// Overhead multiplier for speedtest results
    /// </summary>
    public double OverheadMultiplier => GetOverheadMultiplier();

    /// <summary>
    /// Baseline latency in milliseconds (typical unloaded ping)
    /// </summary>
    public double BaselineLatency => GetBaselineLatency();

    /// <summary>
    /// Latency threshold for triggering rate adjustments
    /// </summary>
    public double LatencyThreshold => GetLatencyThreshold();

    /// <summary>
    /// Rate decrease multiplier when high latency detected
    /// </summary>
    public double LatencyDecrease => GetLatencyDecrease();

    /// <summary>
    /// Rate increase multiplier when latency normalizes
    /// </summary>
    public double LatencyIncrease => GetLatencyIncrease();

    /// <summary>
    /// Ping target host for latency monitoring
    /// </summary>
    public string PingHost { get; set; } = "1.1.1.1";

    /// <summary>
    /// Preferred speedtest server ID. Defaults based on connection type.
    /// Starlink defaults to 59762 (BBR-enabled server near common Starlink PoPs).
    /// </summary>
    public string? PreferredSpeedtestServerId
    {
        get => _preferredSpeedtestServerId ?? GetDefaultSpeedtestServer();
        set => _preferredSpeedtestServerId = value;
    }
    private string? _preferredSpeedtestServerId;

    /// <summary>
    /// Get default speedtest server based on connection type
    /// </summary>
    private string? GetDefaultSpeedtestServer()
    {
        return Type switch
        {
            // Starlink: BBR-enabled server near common Starlink PoPs
            ConnectionType.Starlink => "59762",
            // Other connection types: let Ookla auto-select
            _ => null
        };
    }

    /// <summary>
    /// Calculate maximum speed based on connection type characteristics
    /// </summary>
    private int CalculateMaxSpeed(int nominalSpeed)
    {
        return Type switch
        {
            // Fiber often exceeds advertised speeds
            ConnectionType.Fiber => (int)(nominalSpeed * 1.05),

            // DOCSIS cable typically hits 95% of advertised
            ConnectionType.DocsisCable => (int)(nominalSpeed * 0.95),

            // Starlink: can exceed nominal by ~10%
            ConnectionType.Starlink => (int)(nominalSpeed * 1.10),

            // DSL is distance-limited, rarely exceeds nominal
            ConnectionType.Dsl => (int)(nominalSpeed * 0.95),

            // Fixed wireless varies with conditions
            ConnectionType.FixedWireless => (int)(nominalSpeed * 1.10),

            // Cellular varies significantly
            ConnectionType.CellularHome => (int)(nominalSpeed * 1.20),

            _ => nominalSpeed
        };
    }

    /// <summary>
    /// Calculate minimum (floor) speed based on connection type
    /// </summary>
    private int CalculateMinSpeed(int nominalSpeed)
    {
        return Type switch
        {
            // Fiber is very consistent
            ConnectionType.Fiber => (int)(nominalSpeed * 0.90),

            // DOCSIS can drop during peak congestion
            ConnectionType.DocsisCable => (int)(nominalSpeed * 0.65),

            // Starlink: wide variation, can drop to 35% of nominal
            ConnectionType.Starlink => (int)(nominalSpeed * 0.35),

            // DSL is consistent once synced
            ConnectionType.Dsl => (int)(nominalSpeed * 0.85),

            // Fixed wireless varies with weather/interference
            ConnectionType.FixedWireless => (int)(nominalSpeed * 0.50),

            // Cellular varies with congestion
            ConnectionType.CellularHome => (int)(nominalSpeed * 0.40),

            _ => (int)(nominalSpeed * 0.5)
        };
    }

    /// <summary>
    /// Calculate absolute maximum for rate limiting
    /// </summary>
    private int CalculateAbsoluteMax(int nominalSpeed)
    {
        return Type switch
        {
            ConnectionType.Fiber => (int)(nominalSpeed * 1.02),
            ConnectionType.DocsisCable => (int)(nominalSpeed * 0.98),
            ConnectionType.Starlink => (int)(nominalSpeed * 1.15),
            ConnectionType.Dsl => (int)(nominalSpeed * 0.98),
            ConnectionType.FixedWireless => (int)(nominalSpeed * 1.15),
            ConnectionType.CellularHome => (int)(nominalSpeed * 1.25),
            _ => nominalSpeed
        };
    }

    /// <summary>
    /// Get overhead multiplier based on connection type variability
    /// </summary>
    private double GetOverheadMultiplier()
    {
        return Type switch
        {
            // Fiber: minimal overhead needed
            ConnectionType.Fiber => 1.02,

            // DOCSIS: small overhead for peak-hour variation
            ConnectionType.DocsisCable => 1.05,

            // Starlink: larger overhead due to variability
            ConnectionType.Starlink => 1.15,

            // DSL: consistent, minimal overhead
            ConnectionType.Dsl => 1.03,

            // Fixed wireless: moderate overhead
            ConnectionType.FixedWireless => 1.10,

            // Cellular: higher overhead for congestion variability
            ConnectionType.CellularHome => 1.12,

            _ => 1.05
        };
    }

    /// <summary>
    /// Get typical baseline latency for connection type
    /// </summary>
    private double GetBaselineLatency()
    {
        return Type switch
        {
            ConnectionType.Fiber => 5.0,
            ConnectionType.DocsisCable => 18.0,
            ConnectionType.Starlink => 25.0,
            ConnectionType.Dsl => 20.0,
            ConnectionType.FixedWireless => 15.0,
            ConnectionType.CellularHome => 35.0,
            _ => 20.0
        };
    }

    /// <summary>
    /// Get latency threshold for triggering adjustments
    /// </summary>
    private double GetLatencyThreshold()
    {
        return Type switch
        {
            // Fiber: tight threshold, consistent connection
            ConnectionType.Fiber => 2.0,

            // DOCSIS: moderate threshold
            ConnectionType.DocsisCable => 2.5,

            // Starlink: wider threshold due to satellite variation
            ConnectionType.Starlink => 4.0,

            // DSL: moderate threshold
            ConnectionType.Dsl => 3.0,

            // Fixed wireless: wider threshold
            ConnectionType.FixedWireless => 4.0,

            // Cellular: widest threshold due to network variability
            ConnectionType.CellularHome => 5.0,

            _ => 3.0
        };
    }

    /// <summary>
    /// Get rate decrease multiplier for high latency events
    /// </summary>
    private double GetLatencyDecrease()
    {
        return Type switch
        {
            ConnectionType.Fiber => 0.98,
            ConnectionType.DocsisCable => 0.97,
            ConnectionType.Starlink => 0.97,
            ConnectionType.Dsl => 0.97,
            ConnectionType.FixedWireless => 0.96,
            ConnectionType.CellularHome => 0.95,
            _ => 0.97
        };
    }

    /// <summary>
    /// Get rate increase multiplier when latency normalizes
    /// </summary>
    private double GetLatencyIncrease()
    {
        return Type switch
        {
            ConnectionType.Fiber => 1.03,
            ConnectionType.DocsisCable => 1.04,
            ConnectionType.Starlink => 1.04,
            ConnectionType.Dsl => 1.03,
            ConnectionType.FixedWireless => 1.05,
            ConnectionType.CellularHome => 1.05,
            _ => 1.04
        };
    }

    /// <summary>
    /// Get 168-hour baseline dictionary scaled to nominal speed.
    /// Keys are "day_hour" format (0=Mon, 6=Sun), values are speeds in Mbps.
    /// </summary>
    public Dictionary<string, string> GetHourlyBaseline()
    {
        var baseline = new Dictionary<string, string>();
        var pattern = GetBaselinePattern();

        for (int day = 0; day < 7; day++)
        {
            for (int hour = 0; hour < 24; hour++)
            {
                var key = $"{day}_{hour}";
                // Scale the pattern percentage by nominal speed
                var speed = (int)(pattern[day, hour] * NominalDownloadMbps);
                baseline[key] = speed.ToString();
            }
        }

        return baseline;
    }

    /// <summary>
    /// Get blending ratios for baseline/measured speed mixing.
    /// Returns (withinThreshold, belowThreshold) as (baseline%, measured%) tuples.
    /// </summary>
    public (double baselineWeight, double measuredWeight) GetBlendingRatios(bool withinThreshold)
    {
        return Type switch
        {
            // Starlink: more trust in measured speed due to high variability
            ConnectionType.Starlink => withinThreshold
                ? (0.50, 0.50)  // 50/50 average when close to baseline
                : (0.70, 0.30), // 70/30 favor baseline when below

            // DOCSIS: trust baseline more (stable connection)
            ConnectionType.DocsisCable => withinThreshold
                ? (0.60, 0.40)  // 60/40 favor baseline when close
                : (0.80, 0.20), // 80/20 heavily favor baseline when below

            // Fiber: very stable, trust baseline heavily
            ConnectionType.Fiber => withinThreshold
                ? (0.70, 0.30)
                : (0.85, 0.15),

            // DSL: stable once synced
            ConnectionType.Dsl => withinThreshold
                ? (0.65, 0.35)
                : (0.80, 0.20),

            // Variable connections: balance baseline and measured
            ConnectionType.FixedWireless or ConnectionType.CellularHome => withinThreshold
                ? (0.50, 0.50)
                : (0.65, 0.35),

            _ => withinThreshold ? (0.60, 0.40) : (0.80, 0.20)
        };
    }

    /// <summary>
    /// Get 168-hour baseline pattern as percentage of nominal speed (7 days × 24 hours).
    /// Based on real-world data from DOCSIS and Starlink connections.
    /// Returns [day][hour] where day 0=Monday, 6=Sunday.
    /// </summary>
    private double[,] GetBaselinePattern()
    {
        return Type switch
        {
            // DOCSIS Cable: stable with predictable peak-hour congestion
            // Based on ~300 Mbps nominal: 225-262 Mbps range
            ConnectionType.DocsisCable => new double[,]
            {
                // Monday (day 0)
                { 0.87, 0.87, 0.87, 0.87, 0.87, 0.87, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.75, 0.75, 0.75, 0.75, 0.87, 0.87 },
                // Tuesday (day 1)
                { 0.87, 0.87, 0.87, 0.87, 0.87, 0.87, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.75, 0.75, 0.75, 0.75, 0.87, 0.87 },
                // Wednesday (day 2)
                { 0.87, 0.87, 0.87, 0.87, 0.87, 0.87, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.75, 0.75, 0.75, 0.75, 0.87, 0.87 },
                // Thursday (day 3)
                { 0.87, 0.87, 0.87, 0.87, 0.87, 0.87, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.75, 0.75, 0.75, 0.75, 0.87, 0.87 },
                // Friday (day 4)
                { 0.87, 0.87, 0.87, 0.87, 0.87, 0.87, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.75, 0.75, 0.75, 0.75, 0.87, 0.87 },
                // Saturday (day 5)
                { 0.87, 0.87, 0.87, 0.87, 0.87, 0.87, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.75, 0.75, 0.75, 0.75, 0.87, 0.87 },
                // Sunday (day 6) - slightly better evening
                { 0.87, 0.87, 0.87, 0.87, 0.87, 0.87, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.77, 0.77, 0.77, 0.79, 0.85, 0.87 }
            },

            // Starlink: highly variable per day/hour - based on ~400 Mbps nominal
            // Real data normalized from 150-417 Mbps observations
            ConnectionType.Starlink => new double[,]
            {
                // Monday (day 0)
                { 0.75, 0.77, 0.47, 0.46, 0.44, 0.44, 0.41, 0.91, 0.66, 0.42, 0.41, 0.38, 0.80, 0.76, 0.73, 0.71, 0.68, 0.65, 0.42, 0.85, 0.51, 0.48, 0.43, 0.38 },
                // Tuesday (day 1)
                { 0.90, 0.98, 0.78, 0.84, 0.86, 0.73, 0.89, 0.79, 0.87, 0.85, 0.72, 0.74, 0.74, 0.68, 0.58, 0.84, 0.64, 0.70, 0.49, 0.66, 0.62, 0.59, 0.56, 0.75 },
                // Wednesday (day 2)
                { 0.64, 0.65, 0.56, 0.47, 0.40, 0.42, 0.55, 0.68, 0.73, 0.43, 0.44, 0.38, 1.04, 0.76, 0.61, 0.94, 0.79, 0.65, 0.54, 0.69, 0.73, 0.64, 0.63, 0.80 },
                // Thursday (day 3)
                { 0.72, 0.80, 0.67, 0.50, 0.49, 0.55, 0.48, 0.50, 0.57, 0.88, 0.86, 0.84, 0.82, 0.80, 0.78, 0.65, 0.67, 0.68, 0.66, 0.64, 0.49, 0.39, 0.57, 0.75 },
                // Friday (day 4)
                { 0.59, 0.73, 0.74, 0.59, 0.45, 0.43, 0.44, 0.68, 0.80, 0.55, 0.48, 0.55, 0.45, 0.55, 0.65, 0.60, 0.40, 0.77, 0.77, 0.77, 1.01, 0.74, 0.54, 0.73 },
                // Saturday (day 5)
                { 0.64, 0.56, 0.85, 0.76, 0.69, 0.58, 0.53, 0.54, 0.41, 0.62, 0.40, 0.53, 0.66, 0.80, 0.81, 0.74, 0.68, 0.61, 0.55, 0.45, 0.85, 0.74, 0.66, 0.51 },
                // Sunday (day 6)
                { 0.77, 0.75, 0.79, 0.67, 0.49, 0.44, 0.41, 0.43, 0.52, 0.87, 0.71, 0.55, 0.60, 0.51, 0.66, 0.77, 0.72, 0.71, 0.71, 0.70, 0.70, 0.48, 0.41, 0.62 }
            },

            // Fiber: very stable, minimal variation (same pattern all week)
            ConnectionType.Fiber => CreateUniformWeekPattern(new double[]
                { 0.98, 0.98, 0.98, 0.98, 0.98, 0.98, 0.97, 0.97, 0.97, 0.97, 0.97, 0.97, 0.97, 0.97, 0.97, 0.97, 0.97, 0.97, 0.95, 0.95, 0.95, 0.95, 0.98, 0.98 }),

            // DSL: stable but may have minor peak-hour drops (same pattern all week)
            ConnectionType.Dsl => CreateUniformWeekPattern(new double[]
                { 0.92, 0.92, 0.92, 0.92, 0.92, 0.92, 0.90, 0.90, 0.90, 0.90, 0.90, 0.90, 0.90, 0.90, 0.90, 0.90, 0.90, 0.90, 0.85, 0.85, 0.85, 0.85, 0.92, 0.92 }),

            // Fixed Wireless: weather and time-of-day sensitive (same pattern all week)
            ConnectionType.FixedWireless => CreateUniformWeekPattern(new double[]
                { 0.85, 0.85, 0.85, 0.85, 0.85, 0.85, 0.80, 0.80, 0.80, 0.75, 0.75, 0.75, 0.75, 0.75, 0.75, 0.70, 0.70, 0.70, 0.65, 0.65, 0.65, 0.70, 0.80, 0.85 }),

            // Cellular: cell congestion patterns (same pattern all week)
            ConnectionType.CellularHome => CreateUniformWeekPattern(new double[]
                { 0.90, 0.90, 0.90, 0.90, 0.90, 0.85, 0.75, 0.70, 0.70, 0.75, 0.75, 0.75, 0.70, 0.70, 0.70, 0.70, 0.65, 0.60, 0.55, 0.55, 0.60, 0.70, 0.80, 0.85 }),

            _ => CreateUniformWeekPattern(Enumerable.Repeat(0.85, 24).ToArray())
        };
    }

    /// <summary>
    /// Create a 7-day pattern from a single day pattern (for stable connection types)
    /// </summary>
    private static double[,] CreateUniformWeekPattern(double[] dailyPattern)
    {
        var result = new double[7, 24];
        for (int day = 0; day < 7; day++)
        {
            for (int hour = 0; hour < 24; hour++)
            {
                result[day, hour] = dailyPattern[hour];
            }
        }
        return result;
    }

    /// <summary>
    /// Create an SqmConfiguration from this profile
    /// </summary>
    public SqmConfiguration ToSqmConfiguration()
    {
        return new SqmConfiguration
        {
            Interface = Interface,
            MaxDownloadSpeed = MaxDownloadMbps,
            MinDownloadSpeed = MinDownloadMbps,
            AbsoluteMaxDownloadSpeed = AbsoluteMaxDownloadMbps,
            OverheadMultiplier = OverheadMultiplier,
            PingHost = PingHost,
            BaselineLatency = BaselineLatency,
            LatencyThreshold = LatencyThreshold,
            LatencyDecrease = LatencyDecrease,
            LatencyIncrease = LatencyIncrease
        };
    }

    /// <summary>
    /// Get a descriptive string for the connection type
    /// </summary>
    public static string GetConnectionTypeName(ConnectionType type)
    {
        return type switch
        {
            ConnectionType.DocsisCable => "DOCSIS Cable",
            ConnectionType.Starlink => "Starlink",
            ConnectionType.Fiber => "Fiber (FTTH)",
            ConnectionType.Dsl => "DSL",
            ConnectionType.FixedWireless => "Fixed Wireless (WISP)",
            ConnectionType.CellularHome => "Fixed LTE/5G",
            _ => type.ToString()
        };
    }

    /// <summary>
    /// Get a description of the connection type characteristics
    /// </summary>
    public static string GetConnectionTypeDescription(ConnectionType type)
    {
        return type switch
        {
            ConnectionType.DocsisCable => "Stable with peak-hour congestion (190-285 Mbps typical for 300 Mbps plan)",
            ConnectionType.Starlink => "Variable speeds (50-300+ Mbps), weather-sensitive, 20-80ms latency",
            ConnectionType.Fiber => "Very stable, low latency (~5ms), typically exceeds advertised speeds",
            ConnectionType.Dsl => "Stable but speed limited by distance from DSLAM, 10-100 Mbps typical",
            ConnectionType.FixedWireless => "Variable (25-500 Mbps), weather and interference sensitive",
            ConnectionType.CellularHome => "Variable (100-1000 Mbps), cell congestion affects speeds",
            _ => ""
        };
    }
}
