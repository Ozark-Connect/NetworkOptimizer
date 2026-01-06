using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests;

public class AuditScorerTests
{
    private readonly AuditScorer _scorer;
    private readonly Mock<ILogger<AuditScorer>> _loggerMock;

    public AuditScorerTests()
    {
        _loggerMock = new Mock<ILogger<AuditScorer>>();
        _scorer = new AuditScorer(_loggerMock.Object);
    }

    #region CalculateScore Tests

    [Fact]
    public void CalculateScore_NoIssues_Returns100()
    {
        // Arrange
        var result = CreateAuditResult();

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert
        score.Should().Be(100);
    }

    [Fact]
    public void CalculateScore_SingleCriticalIssue_DeductsPoints()
    {
        // Arrange
        var result = CreateAuditResult(criticalIssues: new[]
        {
            CreateIssue(AuditSeverity.Critical, scoreImpact: 10)
        });

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert
        score.Should().Be(90);
    }

    [Fact]
    public void CalculateScore_CriticalDeductionsCappedAt50()
    {
        // Arrange - 10 critical issues with 10 points each = 100, but should cap at 50
        var criticalIssues = Enumerable.Range(0, 10)
            .Select(_ => CreateIssue(AuditSeverity.Critical, scoreImpact: 10))
            .ToArray();
        var result = CreateAuditResult(criticalIssues: criticalIssues);

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert - 100 - 50 (capped) = 50
        score.Should().Be(50);
    }

    [Fact]
    public void CalculateScore_RecommendedDeductionsCappedAt30()
    {
        // Arrange - Many recommended issues exceeding cap
        var recommendedIssues = Enumerable.Range(0, 10)
            .Select(_ => CreateIssue(AuditSeverity.Recommended, scoreImpact: 10))
            .ToArray();
        var result = CreateAuditResult(recommendedIssues: recommendedIssues);

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert - 100 - 30 (capped) = 70
        score.Should().Be(70);
    }

    [Fact]
    public void CalculateScore_InformationalDeductionsCappedAt10()
    {
        // Arrange - Many investigate issues exceeding cap
        var informationalIssues = Enumerable.Range(0, 10)
            .Select(_ => CreateIssue(AuditSeverity.Informational, scoreImpact: 5))
            .ToArray();
        var result = CreateAuditResult(informationalIssues: informationalIssues);

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert - 100 - 10 (capped) = 90
        score.Should().Be(90);
    }

    [Fact]
    public void CalculateScore_AllSeveritiesMaxDeduction_Returns10()
    {
        // Arrange - Max deductions from all severity levels: 50 + 30 + 10 = 90
        var criticalIssues = Enumerable.Range(0, 10)
            .Select(_ => CreateIssue(AuditSeverity.Critical, scoreImpact: 10))
            .ToArray();
        var recommendedIssues = Enumerable.Range(0, 10)
            .Select(_ => CreateIssue(AuditSeverity.Recommended, scoreImpact: 10))
            .ToArray();
        var informationalIssues = Enumerable.Range(0, 10)
            .Select(_ => CreateIssue(AuditSeverity.Informational, scoreImpact: 5))
            .ToArray();

        var result = CreateAuditResult(
            criticalIssues: criticalIssues,
            recommendedIssues: recommendedIssues,
            informationalIssues: informationalIssues);

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert - 100 - 50 - 30 - 10 = 10
        score.Should().Be(10);
    }

    [Fact]
    public void CalculateScore_HardeningBonus_80PercentHardening_Adds5Points()
    {
        // Arrange
        var result = CreateAuditResult(
            hardeningPercentage: 80,
            hardeningMeasureCount: 0);

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert - 100 + 5 bonus, capped at 100
        score.Should().Be(100);
    }

    [Fact]
    public void CalculateScore_HardeningBonus_60PercentHardening_Adds3Points()
    {
        // Arrange - Has some issues to see bonus effect
        var result = CreateAuditResult(
            criticalIssues: new[] { CreateIssue(AuditSeverity.Critical, scoreImpact: 10) },
            hardeningPercentage: 60,
            hardeningMeasureCount: 0);

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert - 100 - 10 + 3 = 93
        score.Should().Be(93);
    }

    [Fact]
    public void CalculateScore_HardeningBonus_4Measures_Adds3Points()
    {
        // Arrange
        var result = CreateAuditResult(
            criticalIssues: new[] { CreateIssue(AuditSeverity.Critical, scoreImpact: 10) },
            hardeningPercentage: 0,
            hardeningMeasureCount: 4);

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert - 100 - 10 + 3 = 93
        score.Should().Be(93);
    }

    [Fact]
    public void CalculateScore_HardeningBonus_MaxBonus_Is8Points()
    {
        // Arrange - 80% hardening (5 points) + 4 measures (3 points) = 8 points
        var result = CreateAuditResult(
            criticalIssues: new[] { CreateIssue(AuditSeverity.Critical, scoreImpact: 20) },
            hardeningPercentage: 80,
            hardeningMeasureCount: 4);

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert - 100 - 20 + 8 = 88
        score.Should().Be(88);
    }

    [Fact]
    public void CalculateScore_NeverExceeds100()
    {
        // Arrange - Max hardening bonus with no issues
        var result = CreateAuditResult(
            hardeningPercentage: 100,
            hardeningMeasureCount: 10);

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert
        score.Should().Be(100);
    }

    [Fact]
    public void CalculateScore_NeverBelowZero()
    {
        // Arrange - Extreme deductions (though capped, testing boundary)
        var criticalIssues = Enumerable.Range(0, 100)
            .Select(_ => CreateIssue(AuditSeverity.Critical, scoreImpact: 100))
            .ToArray();
        var result = CreateAuditResult(criticalIssues: criticalIssues);

        // Act
        var score = _scorer.CalculateScore(result);

        // Assert - Should be capped at 50 deduction, so 50
        score.Should().BeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region DeterminePosture Tests

    [Theory]
    [InlineData(90, 0, SecurityPosture.Excellent)]
    [InlineData(95, 0, SecurityPosture.Excellent)]
    [InlineData(100, 0, SecurityPosture.Excellent)]
    public void DeterminePosture_Score90Plus_NoCritical_ReturnsExcellent(int score, int criticalCount, SecurityPosture expected)
    {
        var posture = _scorer.DeterminePosture(score, criticalCount);
        posture.Should().Be(expected);
    }

    [Theory]
    [InlineData(75, 0, SecurityPosture.Good)]
    [InlineData(80, 0, SecurityPosture.Good)]
    [InlineData(89, 0, SecurityPosture.Good)]
    public void DeterminePosture_Score75To89_NoCritical_ReturnsGood(int score, int criticalCount, SecurityPosture expected)
    {
        var posture = _scorer.DeterminePosture(score, criticalCount);
        posture.Should().Be(expected);
    }

    [Theory]
    [InlineData(60, 0, SecurityPosture.Fair)]
    [InlineData(65, 0, SecurityPosture.Fair)]
    [InlineData(74, 0, SecurityPosture.Fair)]
    public void DeterminePosture_Score60To74_NoCritical_ReturnsFair(int score, int criticalCount, SecurityPosture expected)
    {
        var posture = _scorer.DeterminePosture(score, criticalCount);
        posture.Should().Be(expected);
    }

    [Theory]
    [InlineData(40, 0, SecurityPosture.NeedsAttention)]
    [InlineData(50, 0, SecurityPosture.NeedsAttention)]
    [InlineData(59, 0, SecurityPosture.NeedsAttention)]
    public void DeterminePosture_Score40To59_NoCritical_ReturnsNeedsAttention(int score, int criticalCount, SecurityPosture expected)
    {
        var posture = _scorer.DeterminePosture(score, criticalCount);
        posture.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0, SecurityPosture.Critical)]
    [InlineData(20, 0, SecurityPosture.Critical)]
    [InlineData(39, 0, SecurityPosture.Critical)]
    public void DeterminePosture_ScoreBelow40_NoCritical_ReturnsCritical(int score, int criticalCount, SecurityPosture expected)
    {
        var posture = _scorer.DeterminePosture(score, criticalCount);
        posture.Should().Be(expected);
    }

    [Theory]
    [InlineData(100, 6, SecurityPosture.Critical)]
    [InlineData(90, 10, SecurityPosture.Critical)]
    public void DeterminePosture_MoreThan5Critical_ReturnsCritical_RegardlessOfScore(int score, int criticalCount, SecurityPosture expected)
    {
        var posture = _scorer.DeterminePosture(score, criticalCount);
        posture.Should().Be(expected);
    }

    [Theory]
    [InlineData(100, 3, SecurityPosture.NeedsAttention)]
    [InlineData(95, 4, SecurityPosture.NeedsAttention)]
    [InlineData(90, 5, SecurityPosture.NeedsAttention)]
    public void DeterminePosture_3To5Critical_ReturnsNeedsAttention_RegardlessOfScore(int score, int criticalCount, SecurityPosture expected)
    {
        var posture = _scorer.DeterminePosture(score, criticalCount);
        posture.Should().Be(expected);
    }

    #endregion

    #region GetPostureDescription Tests

    [Theory]
    [InlineData(SecurityPosture.Excellent, "Excellent - Outstanding security configuration")]
    [InlineData(SecurityPosture.Good, "Good - Solid security posture with minimal issues")]
    [InlineData(SecurityPosture.Fair, "Fair - Acceptable but improvements recommended")]
    [InlineData(SecurityPosture.NeedsAttention, "Needs Attention - Several issues require remediation")]
    [InlineData(SecurityPosture.Critical, "Critical - Immediate attention required")]
    public void GetPostureDescription_ReturnsExpectedDescription(SecurityPosture posture, string expected)
    {
        var description = _scorer.GetPostureDescription(posture);
        description.Should().Be(expected);
    }

    #endregion

    #region GetRecommendations Tests

    [Fact]
    public void GetRecommendations_NoIssues_ReturnsMaintainMessage()
    {
        // Arrange
        var result = CreateAuditResult(hardeningPercentage: 80);

        // Act
        var recommendations = _scorer.GetRecommendations(result);

        // Assert
        recommendations.Should().Contain(r => r.Contains("Maintain current security posture"));
    }

    [Fact]
    public void GetRecommendations_CriticalIssues_ReturnsActionableItems()
    {
        // Arrange
        var result = CreateAuditResult(criticalIssues: new[]
        {
            CreateIssue(AuditSeverity.Critical, scoreImpact: 10, type: "IOT_WRONG_VLAN"),
            CreateIssue(AuditSeverity.Critical, scoreImpact: 10, type: "CAMERA_WRONG_VLAN")
        });

        // Act
        var recommendations = _scorer.GetRecommendations(result);

        // Assert
        recommendations.Should().Contain(r => r.Contains("Address 2 critical issues"));
        recommendations.Should().Contain(r => r.Contains("IoT"));
        recommendations.Should().Contain(r => r.Contains("camera"));
    }

    [Fact]
    public void GetRecommendations_LowHardeningPercentage_ReturnsImproveHardening()
    {
        // Arrange
        var result = CreateAuditResult(hardeningPercentage: 30);

        // Act
        var recommendations = _scorer.GetRecommendations(result);

        // Assert
        recommendations.Should().Contain(r => r.Contains("Improve port hardening"));
    }

    [Fact]
    public void GetRecommendations_ManyUnusedPorts_ReturnsDisableUnused()
    {
        // Arrange
        var result = CreateAuditResult(recommendedIssues: new[]
        {
            CreateIssue(AuditSeverity.Recommended, scoreImpact: 1, type: "UNUSED_PORT"),
            CreateIssue(AuditSeverity.Recommended, scoreImpact: 1, type: "UNUSED_PORT"),
            CreateIssue(AuditSeverity.Recommended, scoreImpact: 1, type: "UNUSED_PORT"),
            CreateIssue(AuditSeverity.Recommended, scoreImpact: 1, type: "UNUSED_PORT")
        });

        // Act
        var recommendations = _scorer.GetRecommendations(result);

        // Assert
        recommendations.Should().Contain(r => r.Contains("Disable") && r.Contains("unused port"));
    }

    #endregion

    #region GetScoreLabel Tests

    [Theory]
    [InlineData(90, "EXCELLENT")]
    [InlineData(95, "EXCELLENT")]
    [InlineData(100, "EXCELLENT")]
    [InlineData(75, "GOOD")]
    [InlineData(80, "GOOD")]
    [InlineData(89, "GOOD")]
    [InlineData(60, "FAIR")]
    [InlineData(65, "FAIR")]
    [InlineData(74, "FAIR")]
    [InlineData(0, "NEEDS ATTENTION")]
    [InlineData(30, "NEEDS ATTENTION")]
    [InlineData(59, "NEEDS ATTENTION")]
    public void GetScoreLabel_ReturnsExpectedLabel(int score, string expectedLabel)
    {
        AuditScorer.GetScoreLabel(score).Should().Be(expectedLabel);
    }

    #endregion

    #region CalculateFilteredScore Tests

    [Fact]
    public void CalculateFilteredScore_NoIssues_Returns100()
    {
        var score = _scorer.CalculateFilteredScore(
            new List<AuditIssue>(),
            new AuditStatistics(),
            0);

        score.Should().Be(100);
    }

    [Fact]
    public void CalculateFilteredScore_WithCriticalIssues_DeductsPoints()
    {
        var issues = new List<AuditIssue>
        {
            CreateIssue(AuditSeverity.Critical, scoreImpact: 10)
        };

        var score = _scorer.CalculateFilteredScore(
            issues,
            new AuditStatistics(),
            0);

        score.Should().Be(90);
    }

    [Fact]
    public void CalculateFilteredScore_WithMixedSeverities_CalculatesCorrectly()
    {
        var issues = new List<AuditIssue>
        {
            CreateIssue(AuditSeverity.Critical, scoreImpact: 10),
            CreateIssue(AuditSeverity.Recommended, scoreImpact: 5),
            CreateIssue(AuditSeverity.Informational, scoreImpact: 2)
        };

        var score = _scorer.CalculateFilteredScore(
            issues,
            new AuditStatistics(),
            0);

        // 100 - 10 - 5 - 2 = 83
        score.Should().Be(83);
    }

    [Fact]
    public void CalculateFilteredScore_WithHardening_AddsBonus()
    {
        var issues = new List<AuditIssue>
        {
            CreateIssue(AuditSeverity.Critical, scoreImpact: 20)
        };
        var stats = new AuditStatistics
        {
            TotalPorts = 100,
            MacRestrictedPorts = 80 // 80% hardening
        };

        var score = _scorer.CalculateFilteredScore(issues, stats, hardeningMeasureCount: 4);

        // 100 - 20 + 5 (80% hardening) + 3 (4 measures) = 88
        score.Should().Be(88);
    }

    #endregion

    #region CalculateHardeningBonus Edge Cases

    [Fact]
    public void CalculateScore_HardeningBonus_40PercentHardening_Adds2Points()
    {
        var result = CreateAuditResult(
            criticalIssues: new[] { CreateIssue(AuditSeverity.Critical, scoreImpact: 10) },
            hardeningPercentage: 40,
            hardeningMeasureCount: 0);

        var score = _scorer.CalculateScore(result);

        // 100 - 10 + 2 = 92
        score.Should().Be(92);
    }

    [Fact]
    public void CalculateScore_HardeningBonus_2Measures_Adds2Points()
    {
        var result = CreateAuditResult(
            criticalIssues: new[] { CreateIssue(AuditSeverity.Critical, scoreImpact: 10) },
            hardeningPercentage: 0,
            hardeningMeasureCount: 2);

        var score = _scorer.CalculateScore(result);

        // 100 - 10 + 2 = 92
        score.Should().Be(92);
    }

    [Fact]
    public void CalculateScore_HardeningBonus_1Measure_Adds1Point()
    {
        var result = CreateAuditResult(
            criticalIssues: new[] { CreateIssue(AuditSeverity.Critical, scoreImpact: 10) },
            hardeningPercentage: 0,
            hardeningMeasureCount: 1);

        var score = _scorer.CalculateScore(result);

        // 100 - 10 + 1 = 91
        score.Should().Be(91);
    }

    #endregion

    #region GetRecommendations Edge Cases

    [Fact]
    public void GetRecommendations_PermissiveFirewallRules_ReturnsRestrictMessage()
    {
        var result = CreateAuditResult(criticalIssues: new[]
        {
            CreateIssue(AuditSeverity.Critical, scoreImpact: 10, type: "PERMISSIVE_RULE_1"),
            CreateIssue(AuditSeverity.Critical, scoreImpact: 10, type: "PERMISSIVE_RULE_2")
        });

        var recommendations = _scorer.GetRecommendations(result);

        recommendations.Should().Contain(r => r.Contains("Restrict") && r.Contains("permissive firewall rule"));
    }

    [Fact]
    public void GetRecommendations_ManyMacRestrictionIssues_ReturnsMacRestrictionMessage()
    {
        var macIssues = Enumerable.Range(0, 6)
            .Select(_ => CreateIssue(AuditSeverity.Recommended, scoreImpact: 1, type: "MAC_NOT_RESTRICTED"))
            .ToArray();
        var result = CreateAuditResult(recommendedIssues: macIssues);

        var recommendations = _scorer.GetRecommendations(result);

        recommendations.Should().Contain(r => r.Contains("Implement MAC restrictions"));
    }

    [Fact]
    public void GetRecommendations_IsolationIssues_ReturnsEnableIsolationMessage()
    {
        var result = CreateAuditResult(recommendedIssues: new[]
        {
            CreateIssue(AuditSeverity.Recommended, scoreImpact: 5, type: "PORT_ISOLATION_DISABLED")
        });

        var recommendations = _scorer.GetRecommendations(result);

        recommendations.Should().Contain(r => r.Contains("Enable port isolation"));
    }

    [Fact]
    public void GetRecommendations_HighUnprotectedPorts_ReturnsSecureMessage()
    {
        var result = CreateAuditResult(hardeningPercentage: 80);
        result.Statistics.ActivePorts = 100;
        result.Statistics.UnprotectedActivePorts = 40; // 40% unprotected

        var recommendations = _scorer.GetRecommendations(result);

        recommendations.Should().Contain(r => r.Contains("Secure") && r.Contains("unprotected active ports"));
    }

    #endregion

    #region GenerateExecutiveSummary Tests

    [Fact]
    public void GenerateExecutiveSummary_NoIssues_ReturnsExcellentMessage()
    {
        // Arrange
        var result = CreateAuditResult();
        result.SecurityScore = 100;
        result.Posture = SecurityPosture.Excellent;
        result.Statistics.TotalPorts = 48;

        // Act
        var summary = _scorer.GenerateExecutiveSummary(result);

        // Assert
        summary.Should().Contain("Excellent");
        summary.Should().Contain("100/100");
        summary.Should().Contain("48 ports");
    }

    [Fact]
    public void GenerateExecutiveSummary_WithIssues_ReturnsWarningMessage()
    {
        // Arrange
        var result = CreateAuditResult(criticalIssues: new[]
        {
            CreateIssue(AuditSeverity.Critical, scoreImpact: 10),
            CreateIssue(AuditSeverity.Critical, scoreImpact: 10)
        });
        result.SecurityScore = 80;
        result.Posture = SecurityPosture.Good;

        // Act
        var summary = _scorer.GenerateExecutiveSummary(result);

        // Assert
        summary.Should().Contain("2 critical issue");
    }

    [Fact]
    public void GenerateExecutiveSummary_WithOnlyRecommendedIssues_ReturnsImprovementMessage()
    {
        // Arrange
        var result = CreateAuditResult(recommendedIssues: new[]
        {
            CreateIssue(AuditSeverity.Recommended, scoreImpact: 5),
            CreateIssue(AuditSeverity.Recommended, scoreImpact: 5),
            CreateIssue(AuditSeverity.Recommended, scoreImpact: 5)
        });
        result.SecurityScore = 85;
        result.Posture = SecurityPosture.Good;

        // Act
        var summary = _scorer.GenerateExecutiveSummary(result);

        // Assert
        summary.Should().Contain("3 recommended improvement");
        summary.Should().NotContain("critical issue");
    }

    [Fact]
    public void GenerateExecutiveSummary_SingleCriticalIssue_UsesSingularForm()
    {
        // Arrange
        var result = CreateAuditResult(criticalIssues: new[]
        {
            CreateIssue(AuditSeverity.Critical, scoreImpact: 10)
        });
        result.SecurityScore = 90;
        result.Posture = SecurityPosture.Excellent;

        // Act
        var summary = _scorer.GenerateExecutiveSummary(result);

        // Assert
        summary.Should().Contain("1 critical issue ");
        summary.Should().NotContain("issues");
    }

    #endregion

    #region Helper Methods

    private static AuditResult CreateAuditResult(
        AuditIssue[]? criticalIssues = null,
        AuditIssue[]? recommendedIssues = null,
        AuditIssue[]? informationalIssues = null,
        double hardeningPercentage = 0,
        int hardeningMeasureCount = 0)
    {
        var allIssues = new List<AuditIssue>();
        if (criticalIssues != null) allIssues.AddRange(criticalIssues);
        if (recommendedIssues != null) allIssues.AddRange(recommendedIssues);
        if (informationalIssues != null) allIssues.AddRange(informationalIssues);

        // Calculate port stats to achieve desired hardening percentage
        var totalPorts = 100;
        var macRestrictedPorts = (int)(hardeningPercentage * totalPorts / 100);

        var hardeningMeasures = Enumerable.Range(0, hardeningMeasureCount)
            .Select(i => $"Measure{i}")
            .ToList();

        return new AuditResult
        {
            Issues = allIssues,
            HardeningMeasures = hardeningMeasures,
            Statistics = new AuditStatistics
            {
                TotalPorts = totalPorts,
                MacRestrictedPorts = macRestrictedPorts,
                DisabledPorts = 0,
                ActivePorts = totalPorts - macRestrictedPorts,
                UnprotectedActivePorts = 0
            }
        };
    }

    private static AuditIssue CreateIssue(AuditSeverity severity, int scoreImpact, string type = "TEST_ISSUE")
    {
        return new AuditIssue
        {
            Type = type,
            Severity = severity,
            Message = $"Test {severity} issue",
            ScoreImpact = scoreImpact
        };
    }

    #endregion
}
