namespace NetworkOptimizer.Audit.Scoring;

/// <summary>
/// Constants for security score calculation.
/// Centralizes all scoring parameters for consistency across the codebase.
/// </summary>
public static class ScoreConstants
{
    /// <summary>
    /// Base score before deductions (perfect score)
    /// </summary>
    public const int BaseScore = 100;

    /// <summary>
    /// Maximum deduction for critical issues (prevents one category from tanking the entire score)
    /// </summary>
    public const int MaxCriticalDeduction = 50;

    /// <summary>
    /// Maximum deduction for recommended issues
    /// </summary>
    public const int MaxRecommendedDeduction = 30;

    /// <summary>
    /// Maximum deduction for investigate/informational issues
    /// </summary>
    public const int MaxInvestigateDeduction = 10;

    /// <summary>
    /// Default score impact for critical severity issues
    /// </summary>
    public const int CriticalImpact = 15;

    /// <summary>
    /// Default score impact for recommended severity issues
    /// </summary>
    public const int RecommendedImpact = 5;

    /// <summary>
    /// Default score impact for investigate/informational severity issues
    /// </summary>
    public const int InvestigateImpact = 2;

    /// <summary>
    /// Score impact for high-risk IoT devices (smart locks, thermostats, hubs)
    /// </summary>
    public const int HighRiskIoTImpact = 10;

    /// <summary>
    /// Score impact for low-risk IoT devices (smart TVs, speakers, etc.)
    /// </summary>
    public const int LowRiskIoTImpact = 3;

    // Score thresholds for posture labels
    public const int ExcellentScoreThreshold = 90;
    public const int GoodScoreThreshold = 75;
    public const int FairScoreThreshold = 60;
    public const int NeedsAttentionScoreThreshold = 40;

    // Hardening bonus thresholds
    public const int ExcellentHardeningPercentage = 80;
    public const int GoodHardeningPercentage = 60;
    public const int FairHardeningPercentage = 40;
    public const int MaxHardeningPercentageBonus = 5;
    public const int ManyHardeningMeasures = 4;
    public const int SomeHardeningMeasures = 2;
    public const int MaxHardeningMeasureBonus = 3;

    // Critical issue count thresholds for posture override
    public const int CriticalPostureIssueCount = 5;
    public const int NeedsAttentionIssueCount = 2;
}
