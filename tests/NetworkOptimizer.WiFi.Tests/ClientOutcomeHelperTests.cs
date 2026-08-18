using FluentAssertions;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class ClientOutcomeHelperTests
{
    /// <summary>One signal band's worth of samples, split across the given number of clients.</summary>
    private static IEnumerable<ClientRateSample> Band(
        int channel, int signalBand, int clients, int samplesEach, double meanMbps) =>
        Enumerable.Range(0, clients).Select(i =>
            new ClientRateSample(channel, $"aa:bb:cc:00:00:{i:x2}", signalBand, samplesEach, meanMbps));

    [Fact]
    public void NoSamples_LeavesThresholdAlone()
    {
        ClientOutcomeHelper.MoveThresholdFactor(null, 1, 6, out var reason).Should().Be(1.0);
        reason.Should().BeNull();
    }

    [Fact]
    public void SameChannel_LeavesThresholdAlone()
    {
        var samples = Band(1, -60, clients: 3, samplesEach: 400, meanMbps: 100).ToList();
        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 1, out _).Should().Be(1.0);
    }

    [Fact]
    public void CandidateOnlyOneChannelKnown_LeavesThresholdAlone()
    {
        var samples = Band(1, -60, clients: 3, samplesEach: 400, meanMbps: 100).ToList();
        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out _).Should().Be(1.0);
    }

    [Fact]
    public void CandidateClearlyBetter_LowersTheBar()
    {
        var samples = Band(1, -60, clients: 2, samplesEach: 300, meanMbps: 100)
            .Concat(Band(6, -60, clients: 2, samplesEach: 300, meanMbps: 130))
            .ToList();

        var factor = ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out var reason);

        factor.Should().Be(ClientOutcomeHelper.ImpetusFactor);
        reason.Should().Contain("ch 6");
    }

    [Fact]
    public void CandidateWorse_RaisesTheBar()
    {
        var samples = Band(1, -60, clients: 2, samplesEach: 125, meanMbps: 100)
            .Concat(Band(6, -60, clients: 2, samplesEach: 125, meanMbps: 80))
            .ToList();

        var factor = ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out var reason);

        factor.Should().Be(ClientOutcomeHelper.VetoFactor);
        reason.Should().NotBeNull();
    }

    [Fact]
    public void ImpetusNeedsMoreEvidenceThanVeto()
    {
        // 250 shared samples: enough to block a move, deliberately not enough to originate one.
        var samples = Band(1, -60, clients: 2, samplesEach: 125, meanMbps: 100)
            .Concat(Band(6, -60, clients: 2, samplesEach: 125, meanMbps: 140))
            .ToList();

        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out _).Should().Be(1.0);
    }

    [Fact]
    public void SingleClientPerChannel_LeavesThresholdAlone()
    {
        var samples = Band(1, -60, clients: 1, samplesEach: 600, meanMbps: 100)
            .Concat(Band(6, -60, clients: 1, samplesEach: 600, meanMbps: 140))
            .ToList();

        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out _)
            .Should().Be(1.0, "one chatty device must not speak for a channel");
    }

    [Fact]
    public void NoOverlappingSignalBands_LeavesThresholdAlone()
    {
        // Near clients on one channel, far clients on the other: a rate difference here would
        // be reporting where the clients were standing, not which channel is better.
        var samples = Band(1, -50, clients: 3, samplesEach: 400, meanMbps: 200)
            .Concat(Band(6, -80, clients: 3, samplesEach: 400, meanMbps: 60))
            .ToList();

        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out _).Should().Be(1.0);
    }

    [Fact]
    public void ComparesWithinSignalBands_NotAcrossThem()
    {
        // Candidate wins in every shared band, but carries extra far-away samples that would
        // drag a naive overall mean below the current channel's.
        var samples = Band(1, -50, clients: 2, samplesEach: 300, meanMbps: 100)
            .Concat(Band(6, -50, clients: 2, samplesEach: 300, meanMbps: 130))
            .Concat(Band(6, -85, clients: 2, samplesEach: 900, meanMbps: 10))
            .ToList();

        ClientOutcomeHelper.MoveThresholdFactor(samples, 1, 6, out _)
            .Should().Be(ClientOutcomeHelper.ImpetusFactor);
    }
}
