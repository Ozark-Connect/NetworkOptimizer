using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit;
using AuditModels = NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Web.Services;

public class AuditService
{
    private readonly ILogger<AuditService> _logger;
    private readonly UniFiConnectionService _connectionService;
    private readonly ConfigAuditEngine _auditEngine;

    // Cache the last audit result
    private AuditResult? _lastAuditResult;
    private DateTime? _lastAuditTime;

    public AuditService(
        ILogger<AuditService> logger,
        UniFiConnectionService connectionService,
        ConfigAuditEngine auditEngine)
    {
        _logger = logger;
        _connectionService = connectionService;
        _auditEngine = auditEngine;
    }

    public AuditResult? LastAuditResult => _lastAuditResult;
    public DateTime? LastAuditTime => _lastAuditTime;

    public async Task<AuditResult> RunAuditAsync(AuditOptions options)
    {
        _logger.LogInformation("Running security audit with options: {@Options}", options);

        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            _logger.LogWarning("Cannot run audit: UniFi controller not connected");
            return new AuditResult
            {
                Score = 0,
                ScoreLabel = "UNAVAILABLE",
                ScoreClass = "poor",
                Issues = new List<AuditIssue>
                {
                    new AuditIssue
                    {
                        Severity = "Critical",
                        Category = "Connection",
                        Title = "Controller Not Connected",
                        Description = "Cannot run security audit without an active connection to the UniFi controller.",
                        Recommendation = "Go to Settings and connect to your UniFi controller first."
                    }
                },
                CompletedAt = DateTime.UtcNow
            };
        }

        try
        {
            // Get raw device data from UniFi API
            var deviceDataJson = await _connectionService.Client.GetDevicesRawJsonAsync();

            if (string.IsNullOrEmpty(deviceDataJson))
            {
                throw new Exception("No device data returned from UniFi API");
            }

            _logger.LogInformation("Running audit engine on device data ({Length} bytes)", deviceDataJson.Length);

            // Run the audit engine
            var auditResult = _auditEngine.RunAudit(deviceDataJson, "Network Audit");

            // Convert audit result to web models
            var webResult = ConvertAuditResult(auditResult, options);

            // Cache the result
            _lastAuditResult = webResult;
            _lastAuditTime = DateTime.UtcNow;

            _logger.LogInformation("Audit complete: Score={Score}, Critical={Critical}, Recommended={Recommended}",
                webResult.Score, webResult.CriticalCount, webResult.WarningCount);

            return webResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running security audit");

            return new AuditResult
            {
                Score = 0,
                ScoreLabel = "ERROR",
                ScoreClass = "poor",
                Issues = new List<AuditIssue>
                {
                    new AuditIssue
                    {
                        Severity = "Critical",
                        Category = "System",
                        Title = "Audit Failed",
                        Description = $"An error occurred while running the security audit: {ex.Message}",
                        Recommendation = "Check the logs for more details and ensure the UniFi controller is accessible."
                    }
                },
                CompletedAt = DateTime.UtcNow
            };
        }
    }

    private AuditResult ConvertAuditResult(AuditModels.AuditResult engineResult, AuditOptions options)
    {
        var issues = new List<AuditIssue>();

        foreach (var issue in engineResult.Issues)
        {
            // Filter based on options
            var category = GetCategory(issue.Type);
            if (!ShouldInclude(category, options))
                continue;

            issues.Add(new AuditIssue
            {
                Severity = ConvertSeverity(issue.Severity),
                Category = category,
                Title = GetIssueTitle(issue.Type, issue.Message),
                Description = issue.Message,
                Recommendation = issue.RecommendedAction ?? GetDefaultRecommendation(issue.Type)
            });
        }

        var criticalCount = issues.Count(i => i.Severity == "Critical");
        var warningCount = issues.Count(i => i.Severity == "Warning");
        var infoCount = issues.Count(i => i.Severity == "Info");

        var score = engineResult.SecurityScore;
        var scoreLabel = engineResult.Posture.ToString().ToUpperInvariant();

        var scoreClass = score switch
        {
            >= 90 => "excellent",
            >= 75 => "good",
            >= 60 => "fair",
            _ => "poor"
        };

        return new AuditResult
        {
            Score = score,
            ScoreLabel = scoreLabel,
            ScoreClass = scoreClass,
            CriticalCount = criticalCount,
            WarningCount = warningCount,
            InfoCount = infoCount,
            Issues = issues,
            CompletedAt = DateTime.UtcNow,
            Statistics = new AuditStatistics
            {
                TotalPorts = engineResult.Statistics.TotalPorts,
                ActivePorts = engineResult.Statistics.ActivePorts,
                DisabledPorts = engineResult.Statistics.DisabledPorts,
                MacRestrictedPorts = engineResult.Statistics.MacRestrictedPorts,
                NetworkCount = engineResult.Networks.Count,
                SwitchCount = engineResult.Switches.Count
            },
            HardeningMeasures = engineResult.HardeningMeasures.ToList()
        };
    }

    private static string GetCategory(string issueType) => issueType switch
    {
        "FW_SHADOWED" or "FW_PERMISSIVE" or "FW_ORPHANED" or "FW_ANY_ANY" => "Firewall Rules",
        "VLAN_VIOLATION" or "INTER_VLAN" or "ROUTING_ENABLED" => "VLAN Security",
        "MAC_RESTRICTION" or "UNUSED_PORT" or "PORT_ISOLATION" or "PORT_SECURITY" => "Port Security",
        "DNS_LEAKAGE" => "DNS Security",
        "IOT_WRONG_VLAN" or "CAMERA_WRONG_VLAN" => "Device Placement",
        _ => "General"
    };

    private static bool ShouldInclude(string category, AuditOptions options) => category switch
    {
        "Firewall Rules" => options.IncludeFirewallRules,
        "VLAN Security" => options.IncludeVlanSecurity,
        "Port Security" => options.IncludePortSecurity,
        "DNS Security" => options.IncludeDnsLeakDetection,
        _ => true
    };

    private static string ConvertSeverity(AuditModels.AuditSeverity severity) => severity switch
    {
        AuditModels.AuditSeverity.Critical => "Critical",
        AuditModels.AuditSeverity.Recommended => "Warning",
        AuditModels.AuditSeverity.Investigate => "Info",
        _ => "Info"
    };

    private static string GetIssueTitle(string type, string message)
    {
        // Extract a short title from the issue type
        return type switch
        {
            "FW_SHADOWED" => "Shadowed Firewall Rule",
            "FW_PERMISSIVE" => "Overly Permissive Rule",
            "FW_ORPHANED" => "Orphaned Firewall Rule",
            "FW_ANY_ANY" => "Any-Any Firewall Rule",
            "VLAN_VIOLATION" => "VLAN Policy Violation",
            "INTER_VLAN" => "Inter-VLAN Access Issue",
            "ROUTING_ENABLED" => "Routing on Isolated VLAN",
            "MAC_RESTRICTION" => "Missing MAC Restriction",
            "UNUSED_PORT" => "Unused Port Enabled",
            "PORT_ISOLATION" => "Missing Port Isolation",
            "PORT_SECURITY" => "Port Security Issue",
            "DNS_LEAKAGE" => "DNS Leak Detected",
            "IOT_WRONG_VLAN" => "IoT Device on Wrong VLAN",
            "CAMERA_WRONG_VLAN" => "Camera on Wrong VLAN",
            _ => message.Split('.').FirstOrDefault() ?? type
        };
    }

    private static string GetDefaultRecommendation(string type) => type switch
    {
        "FW_SHADOWED" => "Reorder firewall rules so specific rules appear before broader ones.",
        "FW_PERMISSIVE" => "Tighten the rule to only allow necessary traffic.",
        "FW_ORPHANED" => "Remove rules that reference non-existent objects.",
        "FW_ANY_ANY" => "Replace with specific allow rules for required traffic.",
        "MAC_RESTRICTION" => "Enable MAC-based port security on critical infrastructure ports.",
        "UNUSED_PORT" => "Disable unused ports to reduce attack surface.",
        "PORT_ISOLATION" => "Enable port isolation for security devices.",
        "IOT_WRONG_VLAN" => "Move IoT devices to a dedicated IoT VLAN.",
        "CAMERA_WRONG_VLAN" => "Move cameras to a dedicated Security VLAN.",
        "DNS_LEAKAGE" => "Configure firewall to block direct DNS queries from isolated networks.",
        _ => "Review the configuration and apply security best practices."
    };
}

public class AuditOptions
{
    public bool IncludeFirewallRules { get; set; } = true;
    public bool IncludeVlanSecurity { get; set; } = true;
    public bool IncludePortSecurity { get; set; } = true;
    public bool IncludeDnsLeakDetection { get; set; } = true;
}

public class AuditResult
{
    public int Score { get; set; }
    public string ScoreLabel { get; set; } = "";
    public string ScoreClass { get; set; } = "";
    public int CriticalCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public List<AuditIssue> Issues { get; set; } = new();
    public DateTime CompletedAt { get; set; }
    public AuditStatistics? Statistics { get; set; }
    public List<string> HardeningMeasures { get; set; } = new();
}

public class AuditStatistics
{
    public int TotalPorts { get; set; }
    public int ActivePorts { get; set; }
    public int DisabledPorts { get; set; }
    public int MacRestrictedPorts { get; set; }
    public int NetworkCount { get; set; }
    public int SwitchCount { get; set; }
}

public class AuditIssue
{
    public string Severity { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Recommendation { get; set; } = "";
}
