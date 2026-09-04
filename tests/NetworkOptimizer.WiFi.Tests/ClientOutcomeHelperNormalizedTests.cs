using FluentAssertions;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class ClientOutcomeHelperNormalizedTests
{
    private static readonly DateTime Day0 = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Three days of twenty windows each, one bucket, for one channel.</summary>
    private static IEnumerable<ClientRateSample> Windows(int channel, double raw, double? normalized) =>
        Enumerable.Range(0, 3).Select(d => new ClientRateSample(channel, 80, -60, Day0.AddDays(d), 20, raw, normalized));

    [Fact]
    public void Normalized_rates_are_compared_when_every_window_carries_one()
    {
        // Raw says the candidate doubles the rate; per stream per 20 MHz they are the same radio
        // serving 4x4 laptops on one channel and 1x1 phones on the other.
        var samples = Windows(36, 100, 25).Concat(Windows(149, 200, 25)).ToList();

        var factor = ClientOutcomeHelper.MoveThresholdFactor(samples, 36, 149, out var reason);

        factor.Should().Be(1.0);
        reason.Should().Contain("inconclusive");
    }

    [Fact]
    public void One_console_window_and_the_raw_rate_stands_for_all()
    {
        var samples = Windows(36, 100, 25).Concat(Windows(149, 200, 25)).ToList();
        samples[0] = samples[0] with { NormalizedTxRateMbps = null };

        var factor = ClientOutcomeHelper.MoveThresholdFactor(samples, 36, 149, out _);

        factor.Should().Be(ClientOutcomeHelper.ImpetusFactor);
    }
}
