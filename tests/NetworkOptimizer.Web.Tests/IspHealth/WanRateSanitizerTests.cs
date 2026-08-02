using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// A counter reset at a link flap reports a rate the line cannot carry, and one such sample marks its
/// window loaded - pulling the flap's own loss into Loaded Loss. Modeled on a real capture: 1.09 Gbps
/// on a 350/350 plan, in the same second as an outage.
/// </summary>
public class WanRateSanitizerTests
{
    private static readonly IspHealthOptions Options = new();

    private static ThroughputSample At(int minute, double downBps, double upBps) =>
        new(TestSeries.Start.AddMinutes(minute), downBps, upBps);

    [Fact]
    public void Counter_artifact_is_dropped_and_ordinary_traffic_is_kept()
    {
        var samples = new List<ThroughputSample>
        {
            At(0, 50_000_000, 5_000_000),
            At(1, 1_093_717_987, 1_079_896_299), // the observed artifact
            At(2, 60_000_000, 6_000_000)
        };

        var result = WanRateSanitizer.Filter(samples, expectedDownloadMbps: 350, expectedUploadMbps: 350, Options);

        result.Dropped.Should().Be(1);
        result.Samples.Should().HaveCount(2);
        result.Samples.Should().NotContain(s => s.DownloadBps > 1_000_000_000);
    }

    [Fact]
    public void A_line_beating_its_plan_is_not_treated_as_an_artifact()
    {
        // ISPs over-provision and bursts happen. 1.4x plan is a fast line, not a counter reset, and
        // discarding it would throw away the loaded windows the score needs.
        var samples = new List<ThroughputSample> { At(0, 490_000_000, 480_000_000) };

        WanRateSanitizer.Filter(samples, 350, 350, Options).Dropped.Should().Be(0);
    }

    [Fact]
    public void An_implausible_upload_drops_the_whole_sample()
    {
        // A counter discontinuity corrupts the reading, not one field of it.
        var samples = new List<ThroughputSample> { At(0, 50_000_000, 4_000_000_000) };

        var result = WanRateSanitizer.Filter(samples, 350, 350, Options);
        result.Dropped.Should().Be(1);
        result.Samples.Should().BeEmpty();
    }

    [Fact]
    public void Without_a_configured_plan_nothing_is_judged()
    {
        // No ceiling to compare against, and guessing one risks discarding real traffic.
        var samples = new List<ThroughputSample> { At(0, 8_017_576_149, 6_279_766_857) };

        var result = WanRateSanitizer.Filter(samples, null, null, Options);
        result.Dropped.Should().Be(0);
        result.Samples.Should().HaveCount(1);
    }

    [Fact]
    public void One_direction_configured_still_guards_that_direction()
    {
        var samples = new List<ThroughputSample> { At(0, 1_093_717_987, 5_000_000) };

        WanRateSanitizer.Filter(samples, expectedDownloadMbps: 350, expectedUploadMbps: null, Options)
            .Dropped.Should().Be(1);
    }
}
