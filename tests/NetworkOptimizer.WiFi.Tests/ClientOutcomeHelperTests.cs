using FluentAssertions;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class ClientOutcomeHelperTests
{
    private static readonly DateTime Day0 = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Windows in one signal band, spread across the given number of distinct days.</summary>
    private static IEnumerable<ClientRateSample> Band(
        int channel, int signalBand, int days, int windowsPerDay, double meanMbps) =>
        Enumerable.Range(0, days).Select(d =>
            new ClientRateSample(channel, signalBand, Day0.AddDays(d), windowsPerDay, meanMbps));

    [Fact]
    public void NoSamples_LeavesThresholdAlone()
    {
        ClientOutcomeHelper.MoveThresholdFactor(null, 1, 6, out var reason).Should().Be(1.0);
        reason.Should().Contain("no client history");
    }

    [Fact]
    public void SameChannel_LeavesThresholdAlone()
    {
        var samples = Band(1, -60, days: 5, windowsPerDay: 20, meanMbps: 100).ToList();
        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 1, out _).Should().Be(1.0);
    }

    [Fact]
    public void OnlyOneChannelKnown_LeavesThresholdAlone()
    {
        var samples = Band(1, -60, days: 5, windowsPerDay: 20, meanMbps: 100).ToList();

        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out var reason).Should().Be(1.0);
        reason.Should().Contain("candidate ch 6");
    }

    [Fact]
    public void CandidateClearlyBetter_LowersTheBar()
    {
        var samples = Band(1, -60, days: 5, windowsPerDay: 12, meanMbps: 100)
            .Concat(Band(6, -60, days: 5, windowsPerDay: 12, meanMbps: 130))
            .ToList();

        var factor = ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out var reason);

        factor.Should().Be(ClientOutcomeHelper.ImpetusFactor);
        reason.Should().Contain("ch 6");
    }

    [Fact]
    public void CandidateWorse_RaisesTheBar()
    {
        var samples = Band(1, -60, days: 4, windowsPerDay: 6, meanMbps: 100)
            .Concat(Band(6, -60, days: 4, windowsPerDay: 6, meanMbps: 80))
            .ToList();

        var factor = ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out var reason);

        factor.Should().Be(ClientOutcomeHelper.VetoFactor);
        reason.Should().NotBeNull();
    }

    [Fact]
    public void ImpetusNeedsMoreEvidenceThanVeto()
    {
        // 24 shared windows: enough to block a move, deliberately not enough to originate one.
        var samples = Band(1, -60, days: 4, windowsPerDay: 6, meanMbps: 100)
            .Concat(Band(6, -60, days: 4, windowsPerDay: 6, meanMbps: 140))
            .ToList();

        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out var reason).Should().Be(1.0);
        reason.Should().Contain("inconclusive");
    }

    [Fact]
    public void EvidenceFromTooFewDays_LeavesThresholdAlone()
    {
        // Plenty of windows, but all from a single day - one unusual evening must not decide this.
        var samples = Band(1, -60, days: 1, windowsPerDay: 80, meanMbps: 100)
            .Concat(Band(6, -60, days: 1, windowsPerDay: 80, meanMbps: 140))
            .ToList();

        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out var reason).Should().Be(1.0);
        reason.Should().Contain("too few days");
    }

    [Fact]
    public void NoOverlappingSignalBands_LeavesThresholdAlone()
    {
        // Near clients on one channel, far clients on the other: a rate difference here would
        // be reporting where the clients were standing, not which channel is better.
        var samples = Band(1, -50, days: 5, windowsPerDay: 20, meanMbps: 200)
            .Concat(Band(6, -80, days: 5, windowsPerDay: 20, meanMbps: 60))
            .ToList();

        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out var reason).Should().Be(1.0);
        reason.Should().Contain("no overlapping signal bands");
    }

    [Fact]
    public void ComparesWithinSignalBands_NotAcrossThem()
    {
        // Candidate wins in the shared band, but carries extra far-away windows that would drag a
        // naive overall mean below the current channel's.
        var samples = Band(1, -50, days: 5, windowsPerDay: 12, meanMbps: 100)
            .Concat(Band(6, -50, days: 5, windowsPerDay: 12, meanMbps: 130))
            .Concat(Band(6, -85, days: 5, windowsPerDay: 40, meanMbps: 10))
            .ToList();

        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out _)
            .Should().Be(ClientOutcomeHelper.ImpetusFactor);
    }
}
