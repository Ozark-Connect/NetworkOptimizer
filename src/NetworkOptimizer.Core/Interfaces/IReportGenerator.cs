using NetworkOptimizer.Core.Models;

namespace NetworkOptimizer.Core.Interfaces;

/// <summary>
/// Interface for generating network optimization and audit reports.
/// Provides methods for creating comprehensive reports in various formats.
/// </summary>
public interface IReportGenerator
{
    /// <summary>
    /// Generates a comprehensive network optimization report for a site.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="includeHistoricalData">Include historical performance trends.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Complete optimization report.</returns>
    Task<NetworkOptimizationReport> GenerateOptimizationReportAsync(
        string siteId,
        bool includeHistoricalData = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an executive summary report with key metrics and recommendations.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Executive summary report.</returns>
    Task<ExecutiveSummaryReport> GenerateExecutiveSummaryAsync(
        string siteId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a detailed audit report with all findings and remediation steps.
    /// </summary>
    /// <param name="auditReport">Audit report data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Formatted audit report.</returns>
    Task<FormattedAuditReport> GenerateAuditReportAsync(
        AuditReport auditReport,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a performance comparison report showing before/after metrics.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="startTime">Start of comparison period.</param>
    /// <param name="endTime">End of comparison period.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Performance comparison report.</returns>
    Task<PerformanceComparisonReport> GeneratePerformanceComparisonReportAsync(
        string deviceId,
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a network health dashboard report with real-time metrics.
    /// </summary>
    /// <param name="siteId">Site identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Health dashboard report.</returns>
    Task<HealthDashboardReport> GenerateHealthDashboardAsync(
        string siteId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an agent deployment report with status of all monitoring agents.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Agent deployment report.</returns>
    Task<AgentDeploymentReport> GenerateAgentDeploymentReportAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a report to a specific format (PDF, HTML, JSON, CSV).
    /// </summary>
    /// <param name="report">Report object to export.</param>
    /// <param name="format">Export format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exported report as byte array.</returns>
    Task<byte[]> ExportReportAsync(
        object report,
        ReportFormat format,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules automatic report generation.
    /// </summary>
    /// <param name="schedule">Report schedule configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Schedule identifier.</returns>
    Task<string> ScheduleReportAsync(
        ReportSchedule schedule,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves previously generated reports.
    /// </summary>
    /// <param name="siteId">Site identifier (optional filter).</param>
    /// <param name="reportType">Report type filter (optional).</param>
    /// <param name="startDate">Start date filter (optional).</param>
    /// <param name="endDate">End date filter (optional).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of report metadata.</returns>
    Task<List<ReportMetadata>> GetReportHistoryAsync(
        string? siteId = null,
        ReportType? reportType = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a comprehensive network optimization report.
/// </summary>
public class NetworkOptimizationReport
{
    /// <summary>
    /// Report identifier.
    /// </summary>
    public string ReportId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Report generation timestamp.
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Site identifier.
    /// </summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>
    /// Site name.
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// Executive summary.
    /// </summary>
    public string ExecutiveSummary { get; set; } = string.Empty;

    /// <summary>
    /// Overall network health score (0-100).
    /// </summary>
    public int OverallHealthScore { get; set; }

    /// <summary>
    /// Audit findings summary.
    /// </summary>
    public AuditReport? AuditFindings { get; set; }

    /// <summary>
    /// SQM configuration and performance analysis.
    /// </summary>
    public SqmAnalysis? SqmAnalysis { get; set; }

    /// <summary>
    /// Device inventory and status.
    /// </summary>
    public List<UniFiDevice> Devices { get; set; } = new();

    /// <summary>
    /// Agent deployment status.
    /// </summary>
    public List<AgentStatus> Agents { get; set; } = new();

    /// <summary>
    /// Prioritized recommendations.
    /// </summary>
    public List<OptimizationRecommendation> Recommendations { get; set; } = new();

    /// <summary>
    /// Historical performance trends (if included).
    /// </summary>
    public PerformanceTrends? HistoricalTrends { get; set; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Represents an executive summary report.
/// </summary>
public class ExecutiveSummaryReport
{
    /// <summary>
    /// Report identifier.
    /// </summary>
    public string ReportId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Report generation timestamp.
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Site identifier.
    /// </summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>
    /// Site name.
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// Key metrics summary.
    /// </summary>
    public KeyMetrics Metrics { get; set; } = new();

    /// <summary>
    /// Top issues requiring attention.
    /// </summary>
    public List<string> TopIssues { get; set; } = new();

    /// <summary>
    /// Top recommendations.
    /// </summary>
    public List<string> TopRecommendations { get; set; } = new();

    /// <summary>
    /// Recent improvements made.
    /// </summary>
    public List<string> RecentImprovements { get; set; } = new();
}

/// <summary>
/// Represents a formatted audit report.
/// </summary>
public class FormattedAuditReport
{
    /// <summary>
    /// Report identifier.
    /// </summary>
    public string ReportId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Original audit report data.
    /// </summary>
    public AuditReport AuditData { get; set; } = new();

    /// <summary>
    /// Formatted findings grouped by category.
    /// </summary>
    public Dictionary<string, List<AuditResult>> FindingsByCategory { get; set; } = new();

    /// <summary>
    /// Formatted findings grouped by severity.
    /// </summary>
    public Dictionary<string, List<AuditResult>> FindingsBySeverity { get; set; } = new();

    /// <summary>
    /// Remediation plan with prioritized actions.
    /// </summary>
    public List<RemediationAction> RemediationPlan { get; set; } = new();
}

/// <summary>
/// Represents a performance comparison report.
/// </summary>
public class PerformanceComparisonReport
{
    /// <summary>
    /// Report identifier.
    /// </summary>
    public string ReportId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Device identifier.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Comparison start time.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Comparison end time.
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Before metrics.
    /// </summary>
    public PerformanceBaseline? BeforeMetrics { get; set; }

    /// <summary>
    /// After metrics.
    /// </summary>
    public PerformanceMetrics? AfterMetrics { get; set; }

    /// <summary>
    /// Performance comparison analysis.
    /// </summary>
    public PerformanceComparison? Comparison { get; set; }

    /// <summary>
    /// Trend charts data.
    /// </summary>
    public Dictionary<string, List<double>> TrendData { get; set; } = new();
}

/// <summary>
/// Represents a health dashboard report.
/// </summary>
public class HealthDashboardReport
{
    /// <summary>
    /// Report identifier.
    /// </summary>
    public string ReportId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Report timestamp.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Overall health status (Healthy, Warning, Critical).
    /// </summary>
    public string OverallStatus { get; set; } = string.Empty;

    /// <summary>
    /// Overall health score (0-100).
    /// </summary>
    public int HealthScore { get; set; }

    /// <summary>
    /// Device health summary.
    /// </summary>
    public DeviceHealthSummary DeviceHealth { get; set; } = new();

    /// <summary>
    /// Agent health summary.
    /// </summary>
    public AgentHealthSummary AgentHealth { get; set; } = new();

    /// <summary>
    /// Recent alerts and notifications.
    /// </summary>
    public List<Alert> RecentAlerts { get; set; } = new();

    /// <summary>
    /// Current performance metrics.
    /// </summary>
    public Dictionary<string, double> CurrentMetrics { get; set; } = new();
}

/// <summary>
/// Represents an agent deployment report.
/// </summary>
public class AgentDeploymentReport
{
    /// <summary>
    /// Report identifier.
    /// </summary>
    public string ReportId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Report generation timestamp.
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Total number of deployed agents.
    /// </summary>
    public int TotalAgents { get; set; }

    /// <summary>
    /// Number of online agents.
    /// </summary>
    public int OnlineAgents { get; set; }

    /// <summary>
    /// Number of offline agents.
    /// </summary>
    public int OfflineAgents { get; set; }

    /// <summary>
    /// Agent statuses grouped by type.
    /// </summary>
    public Dictionary<string, List<AgentStatus>> AgentsByType { get; set; } = new();

    /// <summary>
    /// Agent statuses grouped by network segment.
    /// </summary>
    public Dictionary<string, List<AgentStatus>> AgentsBySegment { get; set; } = new();

    /// <summary>
    /// Agents requiring attention.
    /// </summary>
    public List<AgentStatus> ProblematicAgents { get; set; } = new();
}

/// <summary>
/// Report export formats.
/// </summary>
public enum ReportFormat
{
    PDF,
    HTML,
    JSON,
    CSV,
    Markdown
}

/// <summary>
/// Report types.
/// </summary>
public enum ReportType
{
    NetworkOptimization,
    ExecutiveSummary,
    Audit,
    PerformanceComparison,
    HealthDashboard,
    AgentDeployment
}

/// <summary>
/// Represents a scheduled report configuration.
/// </summary>
public class ReportSchedule
{
    /// <summary>
    /// Schedule identifier.
    /// </summary>
    public string ScheduleId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Report type to generate.
    /// </summary>
    public ReportType ReportType { get; set; }

    /// <summary>
    /// Site identifier for the report.
    /// </summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>
    /// Cron expression for scheduling.
    /// </summary>
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>
    /// Export format for the scheduled report.
    /// </summary>
    public ReportFormat ExportFormat { get; set; } = ReportFormat.PDF;

    /// <summary>
    /// Email recipients for the report.
    /// </summary>
    public List<string> EmailRecipients { get; set; } = new();

    /// <summary>
    /// Indicates whether the schedule is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// Represents metadata for a generated report.
/// </summary>
public class ReportMetadata
{
    /// <summary>
    /// Report identifier.
    /// </summary>
    public string ReportId { get; set; } = string.Empty;

    /// <summary>
    /// Report type.
    /// </summary>
    public ReportType ReportType { get; set; }

    /// <summary>
    /// Site identifier.
    /// </summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>
    /// Generation timestamp.
    /// </summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>
    /// Report file path or URL.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Report format.
    /// </summary>
    public ReportFormat Format { get; set; }

    /// <summary>
    /// Report file size in bytes.
    /// </summary>
    public long FileSizeBytes { get; set; }
}

/// <summary>
/// Supporting classes for reports.
/// </summary>
public class SqmAnalysis
{
    public SqmConfiguration? Configuration { get; set; }
    public PerformanceMetrics? CurrentPerformance { get; set; }
    public PerformanceBaseline? Baseline { get; set; }
    public List<string> Recommendations { get; set; } = new();
}

public class PerformanceTrends
{
    public Dictionary<string, List<double>> LatencyTrend { get; set; } = new();
    public Dictionary<string, List<double>> ThroughputTrend { get; set; } = new();
    public Dictionary<string, List<double>> PacketLossTrend { get; set; } = new();
}

public class KeyMetrics
{
    public int DeviceCount { get; set; }
    public int OnlineDeviceCount { get; set; }
    public int HealthScore { get; set; }
    public int CriticalIssueCount { get; set; }
    public double AverageLatencyMs { get; set; }
    public double AverageThroughputMbps { get; set; }
}

public class RemediationAction
{
    public int Priority { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = new();
    public string ExpectedOutcome { get; set; } = string.Empty;
}

public class DeviceHealthSummary
{
    public int TotalDevices { get; set; }
    public int HealthyDevices { get; set; }
    public int WarningDevices { get; set; }
    public int CriticalDevices { get; set; }
    public int OfflineDevices { get; set; }
}

public class AgentHealthSummary
{
    public int TotalAgents { get; set; }
    public int HealthyAgents { get; set; }
    public int WarningAgents { get; set; }
    public int CriticalAgents { get; set; }
    public int OfflineAgents { get; set; }
}

public class Alert
{
    public DateTime Timestamp { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}
