using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Calculates overall security posture score from audit findings
/// Score range: 0-100 (higher is better)
/// </summary>
public class AuditScorer
{
    private readonly ILogger<AuditScorer> _logger;

    // Base score before deductions
    private const int BaseScore = 100;

    // Maximum deductions per severity level
    private const int MaxCriticalDeduction = 50;
    private const int MaxRecommendedDeduction = 30;
    private const int MaxInvestigateDeduction = 10;

    public AuditScorer(ILogger<AuditScorer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculate security posture score (0-100)
    /// </summary>
    public int CalculateScore(AuditResult auditResult)
    {
        var score = BaseScore;
        var deductions = new Dictionary<AuditSeverity, int>();

        // Calculate deductions by severity
        var criticalDeduction = CalculateDeductionForSeverity(
            auditResult.CriticalIssues,
            AuditSeverity.Critical,
            MaxCriticalDeduction);

        var recommendedDeduction = CalculateDeductionForSeverity(
            auditResult.RecommendedIssues,
            AuditSeverity.Recommended,
            MaxRecommendedDeduction);

        var investigateDeduction = CalculateDeductionForSeverity(
            auditResult.InvestigateIssues,
            AuditSeverity.Investigate,
            MaxInvestigateDeduction);

        deductions[AuditSeverity.Critical] = criticalDeduction;
        deductions[AuditSeverity.Recommended] = recommendedDeduction;
        deductions[AuditSeverity.Investigate] = investigateDeduction;

        var totalDeduction = criticalDeduction + recommendedDeduction + investigateDeduction;

        // Apply hardening bonus
        var hardeningBonus = CalculateHardeningBonus(auditResult.Statistics, auditResult.HardeningMeasures.Count);

        score = score - totalDeduction + hardeningBonus;

        // Ensure score stays in 0-100 range
        score = Math.Max(0, Math.Min(100, score));

        _logger.LogInformation(
            "Security Score: {Score}/100 (Critical: -{Critical}, Recommended: -{Recommended}, Investigate: -{Investigate}, Hardening Bonus: +{Bonus})",
            score, criticalDeduction, recommendedDeduction, investigateDeduction, hardeningBonus);

        return score;
    }

    /// <summary>
    /// Calculate deduction for a specific severity level
    /// </summary>
    private int CalculateDeductionForSeverity(List<AuditIssue> issues, AuditSeverity severity, int maxDeduction)
    {
        if (!issues.Any())
            return 0;

        // Sum up score impacts, capped at max deduction
        var totalImpact = issues.Sum(i => i.ScoreImpact);
        var deduction = Math.Min(totalImpact, maxDeduction);

        _logger.LogDebug("{Severity}: {Count} issues, {TotalImpact} points, capped at {Deduction}",
            severity, issues.Count, totalImpact, deduction);

        return deduction;
    }

    /// <summary>
    /// Calculate bonus points for hardening measures
    /// </summary>
    private int CalculateHardeningBonus(AuditStatistics stats, int hardeningMeasureCount)
    {
        var bonus = 0;

        // Bonus for high percentage of hardened ports (max 5 points)
        if (stats.HardeningPercentage >= 80)
            bonus += 5;
        else if (stats.HardeningPercentage >= 60)
            bonus += 3;
        else if (stats.HardeningPercentage >= 40)
            bonus += 2;

        // Bonus for having hardening measures in place (max 3 points)
        if (hardeningMeasureCount >= 4)
            bonus += 3;
        else if (hardeningMeasureCount >= 2)
            bonus += 2;
        else if (hardeningMeasureCount >= 1)
            bonus += 1;

        _logger.LogDebug("Hardening bonus: {Bonus} points ({Percentage:F1}% hardened, {MeasureCount} measures)",
            bonus, stats.HardeningPercentage, hardeningMeasureCount);

        return bonus;
    }

    /// <summary>
    /// Determine overall security posture based on score and critical issues
    /// </summary>
    public SecurityPosture DeterminePosture(int score, int criticalIssues)
    {
        // Critical issues always result in lower posture
        if (criticalIssues > 5)
            return SecurityPosture.Critical;

        if (criticalIssues > 2)
            return SecurityPosture.NeedsAttention;

        // Score-based assessment when few/no critical issues
        return score switch
        {
            >= 90 => SecurityPosture.Excellent,
            >= 75 => SecurityPosture.Good,
            >= 60 => SecurityPosture.Fair,
            >= 40 => SecurityPosture.NeedsAttention,
            _ => SecurityPosture.Critical
        };
    }

    /// <summary>
    /// Get human-readable description of security posture
    /// </summary>
    public string GetPostureDescription(SecurityPosture posture)
    {
        return posture switch
        {
            SecurityPosture.Excellent => "Excellent - Outstanding security configuration",
            SecurityPosture.Good => "Good - Solid security posture with minimal issues",
            SecurityPosture.Fair => "Fair - Acceptable but improvements recommended",
            SecurityPosture.NeedsAttention => "Needs Attention - Several issues require remediation",
            SecurityPosture.Critical => "Critical - Immediate attention required",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Get recommendations based on score and posture
    /// </summary>
    public List<string> GetRecommendations(AuditResult auditResult)
    {
        var recommendations = new List<string>();

        // Critical issues
        if (auditResult.CriticalIssues.Any())
        {
            var criticalCount = auditResult.CriticalIssues.Count;
            recommendations.Add($"Address {criticalCount} critical issue{(criticalCount > 1 ? "s" : "")} immediately");

            // Specific critical issue types
            var iotVlanIssues = auditResult.CriticalIssues.Count(i => i.Type.Contains("IOT"));
            if (iotVlanIssues > 0)
                recommendations.Add($"Move {iotVlanIssues} IoT device{(iotVlanIssues > 1 ? "s" : "")} to dedicated IoT VLAN");

            var cameraIssues = auditResult.CriticalIssues.Count(i => i.Type.Contains("CAMERA"));
            if (cameraIssues > 0)
                recommendations.Add($"Move {cameraIssues} camera{(cameraIssues > 1 ? "s" : "")} to Security VLAN");

            var permissiveRules = auditResult.CriticalIssues.Count(i => i.Type.Contains("PERMISSIVE"));
            if (permissiveRules > 0)
                recommendations.Add($"Restrict {permissiveRules} overly permissive firewall rule{(permissiveRules > 1 ? "s" : "")}");
        }

        // Recommended improvements
        if (auditResult.RecommendedIssues.Any())
        {
            var macRestrictions = auditResult.RecommendedIssues.Count(i => i.Type.Contains("MAC"));
            if (macRestrictions > 5)
                recommendations.Add("Implement MAC restrictions on access ports to prevent unauthorized devices");

            var unusedPorts = auditResult.RecommendedIssues.Count(i => i.Type.Contains("UNUSED"));
            if (unusedPorts > 3)
                recommendations.Add($"Disable {unusedPorts} unused port{(unusedPorts > 1 ? "s" : "")} to reduce attack surface");

            var isolationIssues = auditResult.RecommendedIssues.Count(i => i.Type.Contains("ISOLATION"));
            if (isolationIssues > 0)
                recommendations.Add("Enable port isolation on security-sensitive devices");
        }

        // Low hardening percentage
        if (auditResult.Statistics.HardeningPercentage < 50)
        {
            recommendations.Add($"Improve port hardening (currently {auditResult.Statistics.HardeningPercentage:F0}%)");
        }

        // High number of unprotected active ports
        if (auditResult.Statistics.UnprotectedActivePorts > auditResult.Statistics.ActivePorts * 0.3)
        {
            var percentage = (double)auditResult.Statistics.UnprotectedActivePorts / auditResult.Statistics.ActivePorts * 100;
            recommendations.Add($"Secure {auditResult.Statistics.UnprotectedActivePorts} unprotected active ports ({percentage:F0}% of active ports)");
        }

        // No recommendations means excellent configuration
        if (!recommendations.Any())
        {
            recommendations.Add("Maintain current security posture - no immediate actions required");
            recommendations.Add("Continue monitoring for configuration drift");
        }

        return recommendations;
    }

    /// <summary>
    /// Generate executive summary text
    /// </summary>
    public string GenerateExecutiveSummary(AuditResult auditResult)
    {
        var score = auditResult.SecurityScore;
        var posture = auditResult.Posture;
        var critical = auditResult.CriticalIssues.Count;
        var recommended = auditResult.RecommendedIssues.Count;

        var summary = $"Security Posture: {GetPostureDescription(posture)} (Score: {score}/100)\n\n";

        if (critical == 0 && recommended == 0)
        {
            summary += "Excellent network security configuration with no issues detected. ";
            summary += $"All {auditResult.Statistics.TotalPorts} ports are properly configured.";
        }
        else
        {
            if (critical > 0)
            {
                summary += $"⚠ {critical} critical issue{(critical > 1 ? "s" : "")} requiring immediate attention. ";
            }

            if (recommended > 0)
            {
                summary += $"{recommended} recommended improvement{(recommended > 1 ? "s" : "")} identified. ";
            }

            summary += $"\n\n{auditResult.Statistics.HardeningPercentage:F0}% of ports have security hardening measures applied.";
        }

        return summary;
    }
}
