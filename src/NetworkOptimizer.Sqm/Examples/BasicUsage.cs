using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using System.Text.Json;

namespace NetworkOptimizer.Sqm.Examples;

/// <summary>
/// Example usage of NetworkOptimizer.Sqm
/// </summary>
public class BasicUsage
{
    public static void Example1_ConfigureAndGenerate()
    {
        // Create configuration for your UniFi device
        var config = new SqmConfiguration
        {
            Interface = "eth2",
            MaxDownloadSpeed = 285,
            MinDownloadSpeed = 190,
            AbsoluteMaxDownloadSpeed = 280,
            OverheadMultiplier = 1.05,

            // ISP or upstream provider for latency monitoring
            PingHost = "40.134.217.121",
            BaselineLatency = 17.9,
            LatencyThreshold = 2.2,

            // Adjustment multipliers
            LatencyDecrease = 0.97, // 3% decrease per deviation
            LatencyIncrease = 1.04, // 4% increase for recovery

            // Schedule
            SpeedtestSchedule = new List<string> { "0 6 * * *", "30 18 * * *" },
            PingAdjustmentInterval = 5
        };

        // Validate configuration
        var manager = new SqmManager(config);
        var errors = manager.ValidateConfiguration();

        if (errors.Any())
        {
            Console.WriteLine("Configuration errors:");
            foreach (var error in errors)
            {
                Console.WriteLine($"  - {error}");
            }
            return;
        }

        // Generate scripts
        Console.WriteLine("Generating SQM scripts...");
        manager.GenerateScriptsToDirectory("./sqm-deployment");
        Console.WriteLine("Scripts generated in ./sqm-deployment/");

        Console.WriteLine("\nGenerated files:");
        Console.WriteLine("  - 20-sqm-speedtest-setup.sh (boot script)");
        Console.WriteLine("  - 21-sqm-ping-setup.sh (boot script)");
        Console.WriteLine("  - sqm-speedtest-adjust.sh (speedtest logic)");
        Console.WriteLine("  - sqm-ping-adjust.sh (latency monitoring)");
        Console.WriteLine("  - install.sh (deployment script)");
    }

    public static void Example2_LearningMode()
    {
        var config = new SqmConfiguration
        {
            Interface = "eth2",
            MaxDownloadSpeed = 285,
            MinDownloadSpeed = 190,
            LearningMode = true
        };

        var manager = new SqmManager(config);

        // Start learning mode
        manager.StartLearningMode();
        Console.WriteLine("Learning mode started");

        // Simulate collecting speedtest samples over time
        var samples = new List<(DateTime time, double speed)>
        {
            (DateTime.Parse("2024-01-01 06:00"), 262),
            (DateTime.Parse("2024-01-01 18:30"), 225),
            (DateTime.Parse("2024-01-02 06:00"), 260),
            (DateTime.Parse("2024-01-02 18:30"), 228),
            // ... more samples
        };

        // Process samples
        foreach (var (time, speed) in samples)
        {
            var speedtestJson = CreateMockSpeedtest(speed);
            // In real usage: await manager.TriggerSpeedtest(speedtestJson);
        }

        // Check progress
        var progress = manager.GetLearningProgress();
        Console.WriteLine($"Learning progress: {progress:F1}%");

        if (manager.IsLearningComplete())
        {
            Console.WriteLine("Learning complete! Baseline established for all 168 hours.");
            manager.StopLearningMode();

            // Export baseline
            var baseline = manager.ExportBaselineForScript();
            Console.WriteLine($"\nBaseline has {baseline.Count} entries");

            // Generate scripts with learned baseline
            manager.GenerateScriptsToDirectory("./sqm-with-baseline");
        }
    }

    public static async Task Example3_ProcessSpeedtest()
    {
        var config = new SqmConfiguration
        {
            Interface = "eth2",
            MaxDownloadSpeed = 285,
            MinDownloadSpeed = 190,
            OverheadMultiplier = 1.05
        };

        var manager = new SqmManager(config);

        // Example speedtest JSON from Ookla CLI
        var speedtestJson = @"{
            ""type"": ""result"",
            ""timestamp"": ""2024-01-15T06:00:00Z"",
            ""ping"": {
                ""jitter"": 0.5,
                ""latency"": 18.2,
                ""low"": 17.8,
                ""high"": 19.1
            },
            ""download"": {
                ""bandwidth"": 32500000,
                ""bytes"": 162500000,
                ""elapsed"": 5000,
                ""latency"": {
                    ""iqm"": 18.5,
                    ""low"": 18.0,
                    ""high"": 20.2,
                    ""jitter"": 0.8
                }
            },
            ""upload"": {
                ""bandwidth"": 12000000,
                ""bytes"": 60000000,
                ""elapsed"": 5000
            },
            ""packetLoss"": 0,
            ""isp"": ""Example ISP"",
            ""interface"": {
                ""internalIp"": ""192.168.1.1"",
                ""name"": ""eth2"",
                ""macAddr"": ""00:11:22:33:44:55"",
                ""isVpn"": false,
                ""externalIp"": ""1.2.3.4""
            },
            ""server"": {
                ""id"": 12345,
                ""host"": ""speedtest.example.com"",
                ""port"": 8080,
                ""name"": ""Example Speedtest"",
                ""location"": ""City, ST"",
                ""country"": ""US"",
                ""ip"": ""5.6.7.8""
            },
            ""result"": {
                ""id"": ""abc123"",
                ""url"": ""https://www.speedtest.net/result/abc123"",
                ""persisted"": true
            }
        }";

        // Process speedtest
        var effectiveRate = await manager.TriggerSpeedtest(speedtestJson);
        Console.WriteLine($"Effective rate calculated: {effectiveRate} Mbps");

        // Get status
        var status = manager.GetStatus();
        Console.WriteLine($"\nStatus:");
        Console.WriteLine($"  Current Rate: {status.CurrentRate} Mbps");
        Console.WriteLine($"  Last Speedtest: {status.LastSpeedtest:F1} Mbps");
        Console.WriteLine($"  Baseline Speed: {status.BaselineSpeed ?? 0} Mbps");
        Console.WriteLine($"  Last Adjustment: {status.LastAdjustmentReason}");
    }

    public static void Example4_LatencyMonitoring()
    {
        var config = new SqmConfiguration
        {
            Interface = "eth2",
            PingHost = "40.134.217.121",
            BaselineLatency = 17.9,
            LatencyThreshold = 2.2,
            LatencyDecrease = 0.97,
            LatencyIncrease = 1.04,
            AbsoluteMaxDownloadSpeed = 280
        };

        var manager = new SqmManager(config);

        // Simulate different latency scenarios
        var scenarios = new[]
        {
            (latency: 18.0, currentRate: 265.0, desc: "Normal latency"),
            (latency: 22.5, currentRate: 265.0, desc: "High latency (2 deviations)"),
            (latency: 17.2, currentRate: 240.0, desc: "Reduced latency, rate below optimal"),
            (latency: 18.1, currentRate: 255.0, desc: "Normal latency, rate slightly low")
        };

        Console.WriteLine("Latency Monitoring Scenarios:\n");

        foreach (var (latency, currentRate, desc) in scenarios)
        {
            var (adjustedRate, reason) = manager.ApplyRateAdjustment(latency, currentRate);

            Console.WriteLine($"{desc}:");
            Console.WriteLine($"  Latency: {latency:F1}ms");
            Console.WriteLine($"  Current Rate: {currentRate:F1} Mbps");
            Console.WriteLine($"  Adjusted Rate: {adjustedRate:F1} Mbps");
            Console.WriteLine($"  Reason: {reason}");
            Console.WriteLine();
        }
    }

    public static void Example5_InfluxDBIntegration()
    {
        var config = new SqmConfiguration
        {
            Interface = "eth2",
            MaxDownloadSpeed = 285,
            MinDownloadSpeed = 190,

            // InfluxDB configuration
            InfluxDbEndpoint = "https://influxdb.example.com",
            InfluxDbToken = "your-influxdb-token-here",
            InfluxDbOrg = "your-org",
            InfluxDbBucket = "sqm-metrics"
        };

        var manager = new SqmManager(config);

        // Generate scripts with InfluxDB metrics collector
        var scripts = manager.GenerateScripts();

        if (scripts.ContainsKey("sqm-metrics-collector.sh"))
        {
            Console.WriteLine("InfluxDB metrics collector script generated!");
            Console.WriteLine("\nMetrics collected:");
            Console.WriteLine("  - current_rate: TC rate in Mbps");
            Console.WriteLine("  - latency: Ping latency in ms");
            Console.WriteLine("  - speedtest_speed: Last speedtest result");

            // Save the script
            File.WriteAllText("./sqm-metrics-collector.sh", scripts["sqm-metrics-collector.sh"]);
        }
    }

    public static void Example6_BaselineManagement()
    {
        var config = new SqmConfiguration
        {
            Interface = "eth2",
            MaxDownloadSpeed = 285
        };

        var manager = new SqmManager(config);

        // Get current baseline table
        var baseline = manager.GetBaselineTable();
        Console.WriteLine($"Baseline completeness: {baseline.GetCompletionPercentage():F1}%");
        Console.WriteLine($"Total hours with data: {baseline.Baselines.Count}/168");

        // Export for backup
        var shellFormat = manager.ExportBaselineForScript();
        var json = JsonSerializer.Serialize(shellFormat, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText("baseline-backup.json", json);
        Console.WriteLine("\nBaseline exported to baseline-backup.json");

        // Load baseline from file
        var loadedBaseline = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText("baseline-backup.json")
        );

        if (loadedBaseline != null)
        {
            var newManager = new SqmManager(config);
            var baselineCalculator = new BaselineCalculator();
            baselineCalculator.ImportFromShellFormat(loadedBaseline);
            newManager.LoadBaseline(baselineCalculator.GetBaselineTable());

            Console.WriteLine("Baseline loaded successfully!");
        }
    }

    public static void Example7_CustomSchedules()
    {
        var config = new SqmConfiguration
        {
            Interface = "eth2",
            MaxDownloadSpeed = 285,

            // Custom speedtest schedule (every 4 hours)
            SpeedtestSchedule = new List<string>
            {
                "0 0 * * *",   // Midnight
                "0 4 * * *",   // 4 AM
                "0 8 * * *",   // 8 AM
                "0 12 * * *",  // Noon
                "0 16 * * *",  // 4 PM
                "0 20 * * *"   // 8 PM
            },

            // Check latency every 2 minutes
            PingAdjustmentInterval = 2
        };

        var manager = new SqmManager(config);
        manager.GenerateScriptsToDirectory("./sqm-custom-schedule");

        Console.WriteLine("Generated scripts with custom schedule:");
        Console.WriteLine("  - Speedtest every 4 hours");
        Console.WriteLine("  - Ping adjustment every 2 minutes");
    }

    public static void Example8_RateBounds()
    {
        var config = new SqmConfiguration
        {
            Interface = "eth2",
            AbsoluteMaxDownloadSpeed = 280,
            MaxDownloadSpeed = 285,
            MinDownloadSpeed = 190
        };

        var manager = new SqmManager(config);
        var (minRate, optimalRate, maxRate) = manager.GetRateBounds();

        Console.WriteLine("Rate Bounds:");
        Console.WriteLine($"  Minimum Rate: {minRate} Mbps (hard floor)");
        Console.WriteLine($"  Optimal Rate: {optimalRate} Mbps (94% of max)");
        Console.WriteLine($"  Maximum Rate: {maxRate} Mbps (95% safety cap)");
        Console.WriteLine();
        Console.WriteLine("The system will:");
        Console.WriteLine("  - Decrease rate when latency is high");
        Console.WriteLine($"  - Never go below {minRate} Mbps");
        Console.WriteLine($"  - Increase rate when latency normalizes");
        Console.WriteLine($"  - Never exceed {maxRate} Mbps");
    }

    /// <summary>
    /// Helper method to create mock speedtest JSON
    /// </summary>
    private static string CreateMockSpeedtest(double speedMbps)
    {
        var bandwidthBytesPerSec = (long)(speedMbps * 1_000_000 / 8);

        return $@"{{
            ""type"": ""result"",
            ""timestamp"": ""{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}"",
            ""ping"": {{ ""latency"": 18.0 }},
            ""download"": {{ ""bandwidth"": {bandwidthBytesPerSec} }},
            ""upload"": {{ ""bandwidth"": 12000000 }},
            ""packetLoss"": 0,
            ""isp"": ""Example ISP"",
            ""interface"": {{ ""name"": ""eth2"" }},
            ""server"": {{ ""id"": 12345 }},
            ""result"": {{ ""id"": ""test"" }}
        }}";
    }
}
