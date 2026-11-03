using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// A scheduled audit without a working console connection waits and retries instead of recording a
/// score, then fails with one alert. A thrown audit is a failure, never "completed, Score 0".
/// </summary>
public class ScheduledAuditRetryPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 4, 14, 0, DateTimeKind.Utc);

    private static AuditResult Result(AuditRunStatus status, int score = 0, int critical = 0, int warning = 0) => new()
    {
        RunStatus = status,
        Score = score,
        CriticalCount = critical,
        WarningCount = warning,
        Issues = status == AuditRunStatus.Completed
            ? []
            : [new AuditIssue { Title = "t", Description = "An error occurred while running the security audit: boom" }]
    };

    [Fact]
    public void Completed_ReportsTheScore()
    {
        var outcome = new ScheduledAuditRetryPolicy().Decide("site", Result(AuditRunStatus.Completed, 92, 0, 2), Now);

        outcome.Success.Should().BeTrue();
        outcome.Summary.Should().Be("Score: 92 - 0 critical, 2 recommended");
        outcome.NextRunAt.Should().BeNull();
        outcome.Notify.Should().BeTrue();
        outcome.Status.Should().BeNull();
    }

    [Fact]
    public void ConsoleUnavailable_WaitsQuietlyAndKeepsTheSlot()
    {
        var outcome = new ScheduledAuditRetryPolicy().Decide("site", Result(AuditRunStatus.ConsoleUnavailable), Now);

        outcome.Success.Should().BeTrue();
        outcome.Status.Should().Be("waiting");
        outcome.Notify.Should().BeFalse();
        outcome.KeepSlot.Should().BeTrue();
        outcome.NextRunAt.Should().Be(Now + ScheduledAuditRetryPolicy.RetryDelay);
        outcome.Summary.Should().Contain("retry 1 of 3");
    }

    [Fact]
    public void ConsoleUnavailable_PastTheRetryLimit_FailsWithAnAlert()
    {
        var policy = new ScheduledAuditRetryPolicy();
        for (var i = 0; i < ScheduledAuditRetryPolicy.MaxRetries; i++)
            policy.Decide("site", Result(AuditRunStatus.ConsoleUnavailable), Now).Status.Should().Be("waiting");

        var outcome = policy.Decide("site", Result(AuditRunStatus.ConsoleUnavailable), Now);

        outcome.Success.Should().BeFalse();
        outcome.Notify.Should().BeTrue();
        outcome.NextRunAt.Should().BeNull();
        outcome.KeepSlot.Should().BeFalse();
        outcome.Error.Should().Contain("Nothing was saved");

        // The next slot starts a fresh count
        policy.Decide("site", Result(AuditRunStatus.ConsoleUnavailable), Now).Summary.Should().Contain("retry 1 of 3");
    }

    [Fact]
    public void Completed_AfterRetries_ResetsTheCount()
    {
        var policy = new ScheduledAuditRetryPolicy();
        policy.Decide("site", Result(AuditRunStatus.ConsoleUnavailable), Now);
        policy.Decide("site", Result(AuditRunStatus.ConsoleUnavailable), Now);

        policy.Decide("site", Result(AuditRunStatus.Completed, 100), Now).Success.Should().BeTrue();

        policy.Decide("site", Result(AuditRunStatus.ConsoleUnavailable), Now).Summary.Should().Contain("retry 1 of 3");
    }

    [Fact]
    public void Retries_AreCountedPerSite()
    {
        var policy = new ScheduledAuditRetryPolicy();
        policy.Decide("site-a", Result(AuditRunStatus.ConsoleUnavailable), Now);
        policy.Decide("site-a", Result(AuditRunStatus.ConsoleUnavailable), Now);

        policy.Decide("site-b", Result(AuditRunStatus.ConsoleUnavailable), Now).Summary.Should().Contain("retry 1 of 3");
    }

    [Fact]
    public void Failed_IsAFailureWithTheAuditError()
    {
        var outcome = new ScheduledAuditRetryPolicy().Decide("site", Result(AuditRunStatus.Failed), Now);

        outcome.Success.Should().BeFalse();
        outcome.Notify.Should().BeTrue();
        outcome.Error.Should().Contain("boom");
    }

    [Theory]
    [InlineData(true, true, true, false)]   // same live client, connected
    [InlineData(false, true, true, true)]   // tunnel dropped or console went silent
    [InlineData(true, false, true, true)]   // reconnected mid-audit: a different client
    [InlineData(false, false, false, true)] // client disposed and cleared
    public void ConsoleConnectionLost_WhenTheStartingClientIsNoLongerLiveAndConnected(
        bool connected, bool sameClient, bool liveClientExists, bool expectLost)
    {
        var auditClient = new object();
        object? liveClient = !liveClientExists ? null : sameClient ? auditClient : new object();

        AuditService.ConsoleConnectionLost(connected, liveClient, auditClient).Should().Be(expectLost);
    }
}
