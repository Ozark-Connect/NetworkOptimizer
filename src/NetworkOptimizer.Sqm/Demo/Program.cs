using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using System.Text.Json;

namespace NetworkOptimizer.Sqm.Demo;

/// <summary>
/// Demo program showing NetworkOptimizer.Sqm capabilities
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("NetworkOptimizer.Sqm - SQM Script Generator Demo");
        Console.WriteLine("=================================================\n");

        // Create sample configuration
        var config = CreateSampleConfiguration();

        // Initialize manager
        var manager = new SqmManager(config);

        // Validate configuration
        Console.WriteLine("1. Validating Configuration...");
        var errors = manager.ValidateConfiguration();
        if (errors.Any())
        {
            Console.WriteLine("   Configuration errors found:");
            foreach (var error in errors)
            {
                Console.WriteLine($"   - {error}");
            }
            return;
        }
        Console.WriteLine("   ✓ Configuration valid\n");

        // Show configuration summary
        ShowConfigurationSummary(config);

        // Load sample baseline
        Console.WriteLine("2. Loading Sample Baseline...");
        LoadSampleBaseline(manager);
        var baseline = manager.GetBaselineTable();
        Console.WriteLine($"   ✓ Baseline loaded: {baseline.Baselines.Count} hours configured");
        Console.WriteLine($"   ✓ Completeness: {baseline.GetCompletionPercentage():F1}%\n");

        // Get current baseline
        var currentBaseline = baseline.GetCurrentBaseline();
        if (currentBaseline != null)
        {
            Console.WriteLine($"   Current hour baseline: {currentBaseline.Median:F0} Mbps");
            Console.WriteLine($"   (Day {currentBaseline.DayOfWeek}, Hour {currentBaseline.Hour})\n");
        }

        // Show rate bounds
        Console.WriteLine("3. Rate Bounds:");
        var (minRate, optimalRate, maxRate) = manager.GetRateBounds();
        Console.WriteLine($"   Minimum: {minRate} Mbps");
        Console.WriteLine($"   Optimal: {optimalRate:F1} Mbps");
        Console.WriteLine($"   Maximum: {maxRate:F1} Mbps\n");

        // Simulate latency scenarios
        Console.WriteLine("4. Simulating Latency Scenarios:");
        SimulateLatencyScenarios(manager);

        // Generate scripts
        Console.WriteLine("\n5. Generating Shell Scripts...");
        var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "generated-scripts");
        Directory.CreateDirectory(outputDir);

        manager.GenerateScriptsToDirectory(outputDir);

        var scriptFiles = Directory.GetFiles(outputDir, "*.sh");
        Console.WriteLine($"   ✓ Generated {scriptFiles.Length} scripts in: {outputDir}");
        foreach (var file in scriptFiles)
        {
            var fileInfo = new FileInfo(file);
            Console.WriteLine($"     - {fileInfo.Name} ({fileInfo.Length:N0} bytes)");
        }

        // Show deployment instructions
        Console.WriteLine("\n6. Deployment Instructions:");
        ShowDeploymentInstructions(outputDir);

        Console.WriteLine("\n=================================================");
        Console.WriteLine("Demo Complete!");
        Console.WriteLine("=================================================");
    }

    static SqmConfiguration CreateSampleConfiguration()
    {
        return new SqmConfiguration
        {
            Interface = "eth2",
            MaxDownloadSpeed = 285,
            MinDownloadSpeed = 190,
            AbsoluteMaxDownloadSpeed = 280,
            OverheadMultiplier = 1.05,
            PingHost = "40.134.217.121",
            BaselineLatency = 17.9,
            LatencyThreshold = 2.2,
            LatencyDecrease = 0.97,
            LatencyIncrease = 1.04,
            SpeedtestSchedule = new List<string> { "0 6 * * *", "30 18 * * *" },
            PingAdjustmentInterval = 5
        };
    }

    static void ShowConfigurationSummary(SqmConfiguration config)
    {
        Console.WriteLine("Configuration Summary:");
        Console.WriteLine($"   Interface: {config.Interface}");
        Console.WriteLine($"   Max Download Speed: {config.MaxDownloadSpeed} Mbps");
        Console.WriteLine($"   Min Download Speed: {config.MinDownloadSpeed} Mbps");
        Console.WriteLine($"   Overhead Multiplier: {config.OverheadMultiplier:F2} ({(config.OverheadMultiplier - 1) * 100:F0}%)");
        Console.WriteLine($"   Ping Host: {config.PingHost}");
        Console.WriteLine($"   Baseline Latency: {config.BaselineLatency} ms");
        Console.WriteLine($"   Latency Threshold: {config.LatencyThreshold} ms");
        Console.WriteLine($"   Rate Decrease: {config.LatencyDecrease} ({(1 - config.LatencyDecrease) * 100:F0}% per deviation)");
        Console.WriteLine($"   Rate Increase: {config.LatencyIncrease} ({(config.LatencyIncrease - 1) * 100:F0}% for recovery)");
        Console.WriteLine($"   Speedtest Schedule: {string.Join(", ", config.SpeedtestSchedule)}");
        Console.WriteLine($"   Ping Interval: Every {config.PingAdjustmentInterval} minutes\n");
    }

    static void LoadSampleBaseline(SqmManager manager)
    {
        // Create a sample baseline (simplified version)
        var baseline = new Dictionary<string, string>();

        // Monday-Friday: Fast during off-peak, slower during peak
        for (int day = 0; day < 5; day++) // Monday-Friday
        {
            for (int hour = 0; hour < 24; hour++)
            {
                string speed;
                if (hour >= 18 && hour <= 21) // Evening peak
                {
                    speed = "225";
                }
                else if (hour >= 6 && hour <= 17) // Daytime
                {
                    speed = "255";
                }
                else // Night
                {
                    speed = "262";
                }
                baseline[$"{day}_{hour}"] = speed;
            }
        }

        // Weekend: Similar pattern but slightly different
        for (int day = 5; day < 7; day++) // Saturday-Sunday
        {
            for (int hour = 0; hour < 24; hour++)
            {
                string speed;
                if (hour >= 18 && hour <= 20) // Evening
                {
                    speed = "230";
                }
                else if (hour >= 8 && hour <= 17) // Daytime
                {
                    speed = "255";
                }
                else // Night
                {
                    speed = "262";
                }
                baseline[$"{day}_{hour}"] = speed;
            }
        }

        var calculator = new BaselineCalculator();
        calculator.ImportFromShellFormat(baseline);
        manager.LoadBaseline(calculator.GetBaselineTable());
    }

    static void SimulateLatencyScenarios(SqmManager manager)
    {
        var scenarios = new[]
        {
            new { Latency = 18.0, Rate = 265.0, Name = "Normal latency" },
            new { Latency = 22.5, Rate = 265.0, Name = "High latency (+2 deviations)" },
            new { Latency = 25.0, Rate = 265.0, Name = "Very high latency (+3 deviations)" },
            new { Latency = 17.2, Rate = 240.0, Name = "Reduced latency, low rate" },
            new { Latency = 18.1, Rate = 255.0, Name = "Normal latency, moderate rate" }
        };

        foreach (var scenario in scenarios)
        {
            var (adjustedRate, reason) = manager.ApplyRateAdjustment(scenario.Latency, scenario.Rate);
            var change = adjustedRate - scenario.Rate;
            var changePercent = (change / scenario.Rate) * 100;

            Console.WriteLine($"\n   {scenario.Name}:");
            Console.WriteLine($"     Input: {scenario.Latency:F1}ms latency, {scenario.Rate:F0} Mbps rate");
            Console.WriteLine($"     Output: {adjustedRate:F1} Mbps ({change:+0.0;-0.0} Mbps, {changePercent:+0.0;-0.0}%)");
            Console.WriteLine($"     Reason: {reason}");
        }
    }

    static void ShowDeploymentInstructions(string outputDir)
    {
        Console.WriteLine($@"
   Step 1: Copy scripts to your UniFi device
   -----------------------------------------
   scp -r {outputDir}/* root@YOUR_DEVICE_IP:/tmp/sqm-scripts/

   Step 2: SSH into your device
   -----------------------------
   ssh root@YOUR_DEVICE_IP

   Step 3: Install the scripts
   ---------------------------
   cd /tmp/sqm-scripts
   chmod +x install.sh
   ./install.sh

   Step 4: Monitor the logs
   ------------------------
   tail -f /var/log/sqm-speedtest-adjust.log
   tail -f /var/log/sqm-ping-adjust.log

   Step 5: Check TC configuration
   -------------------------------
   tc class show dev ifbeth2 | grep ""class htb""

   The system will automatically:
   - Run speedtest at scheduled times (6 AM, 6:30 PM)
   - Adjust bandwidth every 5 minutes based on latency
   - Blend with baseline speeds for stability
   - Log all adjustments for monitoring
");
    }
}
