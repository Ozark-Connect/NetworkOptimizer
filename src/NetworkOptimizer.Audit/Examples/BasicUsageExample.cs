using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Models;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Audit.Examples;

/// <summary>
/// Example demonstrating basic usage of the ConfigAuditEngine
/// </summary>
public class BasicUsageExample
{
    /// <summary>
    /// Run a basic audit and display results
    /// </summary>
    public static void RunBasicAudit()
    {
        // Create logger
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger<ConfigAuditEngine>();

        // Create audit engine
        var auditEngine = new ConfigAuditEngine(logger);

        // Run audit from file
        var auditResult = auditEngine.RunAuditFromFile(
            jsonFilePath: "path/to/unifi_devices.json",
            clientName: "Example Corporation"
        );

        // Display summary
        Console.WriteLine("=== AUDIT SUMMARY ===");
        Console.WriteLine($"Client: {auditResult.ClientName}");
        Console.WriteLine($"Security Score: {auditResult.SecurityScore}/100");
        Console.WriteLine($"Security Posture: {auditResult.Posture}");
        Console.WriteLine($"Networks Discovered: {auditResult.Networks.Count}");
        Console.WriteLine($"Switches Discovered: {auditResult.Switches.Count}");
        Console.WriteLine($"Total Ports: {auditResult.Statistics.TotalPorts}");
        Console.WriteLine();

        // Display issues by severity
        Console.WriteLine($"Critical Issues: {auditResult.CriticalIssues.Count}");
        Console.WriteLine($"Recommended Improvements: {auditResult.RecommendedIssues.Count}");
        Console.WriteLine($"Items to Investigate: {auditResult.InvestigateIssues.Count}");
        Console.WriteLine();

        // Display hardening measures
        if (auditResult.HardeningMeasures.Any())
        {
            Console.WriteLine("=== HARDENING MEASURES IN PLACE ===");
            foreach (var measure in auditResult.HardeningMeasures)
            {
                Console.WriteLine($"✓ {measure}");
            }
            Console.WriteLine();
        }

        // Display critical issues
        if (auditResult.CriticalIssues.Any())
        {
            Console.WriteLine("=== CRITICAL ISSUES ===");
            foreach (var issue in auditResult.CriticalIssues)
            {
                Console.WriteLine($"[!] {issue.DeviceName} - Port {issue.Port} ({issue.PortName})");
                Console.WriteLine($"    {issue.Message}");
                if (!string.IsNullOrEmpty(issue.RecommendedAction))
                {
                    Console.WriteLine($"    Action: {issue.RecommendedAction}");
                }
                Console.WriteLine();
            }
        }

        // Get and display recommendations
        var recommendations = auditEngine.GetRecommendations(auditResult);
        if (recommendations.Any())
        {
            Console.WriteLine("=== RECOMMENDATIONS ===");
            for (int i = 0; i < recommendations.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {recommendations[i]}");
            }
            Console.WriteLine();
        }

        // Save results
        auditEngine.SaveResults(auditResult, "audit_results.json", format: "json");
        auditEngine.SaveResults(auditResult, "audit_report.txt", format: "text");

        Console.WriteLine("Results saved to audit_results.json and audit_report.txt");
    }

    /// <summary>
    /// Audit from JSON string (e.g., from API call)
    /// </summary>
    public static void RunAuditFromApiData(string deviceJson)
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<ConfigAuditEngine>();

        var auditEngine = new ConfigAuditEngine(logger);
        var auditResult = auditEngine.RunAudit(deviceJson, clientName: "API Data Audit");

        // Process results
        Console.WriteLine($"Score: {auditResult.SecurityScore}/100");
        Console.WriteLine($"Posture: {auditResult.Posture}");
    }

    /// <summary>
    /// Generate executive summary for management
    /// </summary>
    public static void GenerateExecutiveSummary()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<ConfigAuditEngine>();

        var auditEngine = new ConfigAuditEngine(logger);
        var auditResult = auditEngine.RunAuditFromFile("path/to/unifi_devices.json");

        // Generate summary
        var summary = auditEngine.GenerateExecutiveSummary(auditResult);
        Console.WriteLine(summary);

        // Save summary to file
        File.WriteAllText("executive_summary.txt", summary);
    }

    /// <summary>
    /// Detailed analysis of network topology
    /// </summary>
    public static void AnalyzeNetworkTopology()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<ConfigAuditEngine>();

        var auditEngine = new ConfigAuditEngine(logger);
        var auditResult = auditEngine.RunAuditFromFile("path/to/unifi_devices.json");

        Console.WriteLine("=== NETWORK TOPOLOGY ===");
        Console.WriteLine();

        foreach (var network in auditResult.Networks.OrderBy(n => n.VlanId))
        {
            Console.WriteLine($"Network: {network.Name}");
            Console.WriteLine($"  VLAN ID: {network.VlanId}{(network.IsNative ? " (native)" : "")}");
            Console.WriteLine($"  Purpose: {network.Purpose}");
            Console.WriteLine($"  Subnet: {network.Subnet ?? "N/A"}");
            Console.WriteLine($"  Gateway: {network.Gateway ?? "N/A"}");

            if (network.DnsServers?.Any() ?? false)
            {
                Console.WriteLine($"  DNS: {string.Join(", ", network.DnsServers)}");
            }

            // Count ports on this network
            var portsOnNetwork = auditResult.Switches
                .SelectMany(s => s.Ports)
                .Count(p => p.NativeNetworkId == network.Id && p.IsUp);

            Console.WriteLine($"  Active Ports: {portsOnNetwork}");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Analyze specific switch configuration
    /// </summary>
    public static void AnalyzeSwitchConfiguration()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<ConfigAuditEngine>();

        var auditEngine = new ConfigAuditEngine(logger);
        var auditResult = auditEngine.RunAuditFromFile("path/to/unifi_devices.json");

        Console.WriteLine("=== SWITCH ANALYSIS ===");
        Console.WriteLine();

        foreach (var switchInfo in auditResult.Switches)
        {
            var deviceType = switchInfo.IsGateway ? "[Gateway]" : "[Switch]";

            Console.WriteLine($"{deviceType} {switchInfo.Name}");
            Console.WriteLine($"  Model: {switchInfo.ModelName}");
            Console.WriteLine($"  IP: {switchInfo.IpAddress ?? "N/A"}");
            Console.WriteLine($"  Total Ports: {switchInfo.Ports.Count}");
            Console.WriteLine($"  Active Ports: {switchInfo.Ports.Count(p => p.IsUp)}");
            Console.WriteLine($"  Disabled Ports: {switchInfo.Ports.Count(p => p.ForwardMode == "disabled")}");
            Console.WriteLine($"  MAC Restricted: {switchInfo.Ports.Count(p => p.AllowedMacAddresses?.Any() ?? false)}");

            if (switchInfo.Capabilities.MaxCustomMacAcls > 0)
            {
                Console.WriteLine($"  MAC ACL Support: Yes (max {switchInfo.Capabilities.MaxCustomMacAcls})");
            }
            else
            {
                Console.WriteLine($"  MAC ACL Support: No");
            }

            // Find issues on this switch
            var switchIssues = auditResult.Issues.Where(i => i.DeviceName == switchInfo.Name).ToList();
            if (switchIssues.Any())
            {
                Console.WriteLine($"  Issues: {switchIssues.Count}");
                foreach (var issue in switchIssues.Take(3))
                {
                    Console.WriteLine($"    - Port {issue.Port}: {issue.Message}");
                }
                if (switchIssues.Count > 3)
                {
                    Console.WriteLine($"    ... and {switchIssues.Count - 3} more");
                }
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// Focus on IoT device placement
    /// </summary>
    public static void AnalyzeIoTDevices()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<ConfigAuditEngine>();

        var auditEngine = new ConfigAuditEngine(logger);
        var auditResult = auditEngine.RunAuditFromFile("path/to/unifi_devices.json");

        Console.WriteLine("=== IoT DEVICE ANALYSIS ===");
        Console.WriteLine();

        // Find IoT networks
        var iotNetworks = auditResult.Networks.Where(n => n.Purpose == NetworkPurpose.IoT).ToList();

        if (!iotNetworks.Any())
        {
            Console.WriteLine("⚠ No IoT networks detected!");
            Console.WriteLine("  Consider creating a dedicated IoT VLAN for smart devices.");
            Console.WriteLine();
        }
        else
        {
            foreach (var network in iotNetworks)
            {
                Console.WriteLine($"IoT Network: {network.Name} (VLAN {network.VlanId})");
                Console.WriteLine($"  Subnet: {network.Subnet}");

                var devicesOnNetwork = auditResult.Switches
                    .SelectMany(s => s.Ports)
                    .Where(p => p.NativeNetworkId == network.Id && p.IsUp)
                    .ToList();

                Console.WriteLine($"  Devices: {devicesOnNetwork.Count}");
                Console.WriteLine();
            }
        }

        // Find IoT devices on wrong VLANs
        var iotVlanIssues = auditResult.Issues
            .Where(i => i.Type.Contains("IOT"))
            .ToList();

        if (iotVlanIssues.Any())
        {
            Console.WriteLine("⚠ IoT Devices on Wrong VLANs:");
            foreach (var issue in iotVlanIssues)
            {
                Console.WriteLine($"  - {issue.DeviceName} Port {issue.Port} ({issue.PortName})");
                Console.WriteLine($"    Current: {issue.CurrentNetwork}");
                Console.WriteLine($"    Should be: {issue.RecommendedNetwork}");
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine("✓ All IoT devices are properly placed on IoT VLANs");
        }
    }
}
