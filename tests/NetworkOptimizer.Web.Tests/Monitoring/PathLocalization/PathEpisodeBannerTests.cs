using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathLocalization;

public class PathEpisodeBannerTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 20, 0, 0, DateTimeKind.Utc);

    private static PathEpisodeView Episode(int id, DateTime opened, DateTime? closed, bool tracing = false,
        CongestionDisposition disposition = CongestionDisposition.Unverifiable, double peakLoss = 27, double? baseRtt = 12, double? peakRtt = 13) => new()
    {
        Id = id,
        OpenedAt = opened,
        ClosedAt = closed,
        Tracing = tracing,
        Disposition = disposition,
        Label = "past the last clean hop",
        Degraded = new[]
        {
            new PathEpisodeTargetView(1, "t1", "Target A", PathEpisodeTargetRole.Degraded, baseRtt, peakRtt, 0, peakLoss, null)
        }
    };

    [Fact]
    public void EventRow_NamesTheTargetsOnce()
    {
        var e = Episode(1, DateTime.UtcNow.AddMinutes(-25), null, tracing: true) with
        {
            Label = "Target A and Target B lost packets and slowed down together, but their routes share nothing after their last clean routers. "
                + "The common factor is most likely the return path into Example, which a traceroute can't see.",
            Degraded = new[]
            {
                new PathEpisodeTargetView(1, "t1", "Target A", PathEpisodeTargetRole.Degraded, 12, 25, 0, 12, null),
                new PathEpisodeTargetView(2, "t2", "Target B", PathEpisodeTargetRole.Degraded, 12, 25, 0, 5, null)
            }
        };

        PathEpisodePresentation.Entries(new[] { e }).Single().Text.Should().Be(
            "Target A and Target B are losing packets (peak 12%) and running 13 ms slower, for 25 min so far. "
            + "Their routes share nothing after their last clean routers, so the common factor is most likely the return path into Example, which a traceroute can't see.");

        PathEpisodePresentation.WithoutNames("A and B all cross Lumen (`AS3356`) after their last clean router, and every one shows the problem inside it.")
            .Should().Be("They all cross Lumen (`AS3356`) after their last clean router, and every one shows the problem inside it.");
        PathEpisodePresentation.WithoutNames("The problem starts inside Lumen.").Should().Be("The problem starts inside Lumen.");
    }

    [Fact]
    public void TargetUrl_FramesTheEpisodeOnTheTargetsCategory()
    {
        var e = Episode(1, Now.AddHours(-3), Now.AddHours(-2));
        var transit = e.Degraded[0] with { Category = "Transit", TargetId = "aws-1" };
        var start = new DateTimeOffset(Now.AddHours(-3).AddMinutes(-30)).ToUnixTimeMilliseconds();
        var span = (long)TimeSpan.FromHours(2).TotalMilliseconds;

        PathEpisodePresentation.TargetUrl(e, transit, "wan2", Now).Should().Be(
            $"/monitoring?tab=performance&category=Transit&at={start + span / 2}&span={span}&wan=wan2&target=aws-1");

        // A Custom target is not reached over one WAN; an open episode's window ends now.
        var open = Episode(2, Now.AddMinutes(-10), null, tracing: true);
        var url = PathEpisodePresentation.TargetUrl(open, transit with { Category = "Custom" }, "wan2", Now);
        url.Should().Contain("category=Custom").And.Contain("&wan=all").And.Contain($"&span={(long)TimeSpan.FromMinutes(40).TotalMilliseconds}");
    }

    [Fact]
    public void Tracing_WinsOverANewerFinishedEpisode()
    {
        var b = PathEpisodePresentation.Banner(new[]
        {
            Episode(1, Now.AddHours(-1), null, tracing: true),
            Episode(2, Now.AddMinutes(-30), Now.AddMinutes(-10))
        }, Now);

        b!.EpisodeId.Should().Be(1);
        b.Phase.Should().Be("tracing");
        b.Lead.Should().Be("Tracing now");
        b.Text.Should().StartWith("Target A is losing packets (peak 27%) while the rest");
        b.More.Should().Be(1);
    }

    [Fact]
    public void Open_WithItsTracesUsedUp_IsOngoing()
    {
        var e = Episode(3, Now.AddHours(-1), null, peakLoss: 0, baseRtt: 33, peakRtt: 54) with { RoundsTraced = 10 };

        var b = PathEpisodePresentation.Banner(new[] { e, Episode(4, Now.AddMinutes(-30), Now.AddMinutes(-10)) }, Now)!;
        b.EpisodeId.Should().Be(3);
        b.Phase.Should().Be("ongoing");
        b.Lead.Should().Be("Still slow");
        b.Text.Should().Be("Target A is still running 21 ms slower. Network Optimizer traced it 10 times, and traces it once more after it recovers, to compare.");
        PathEpisodePresentation.BadgeLabel(e).Should().Be("Ongoing");
        PathEpisodePresentation.Entries(new[] { e }).Single().Text.Should().StartWith("Target A is running 21 ms slower, for ").And.Contain(" so far.");

        PathEpisodePresentation.Banner(new[] { e with { Metered = true } }, Now)!.Text
            .Should().Be("Target A is still running 21 ms slower. This WAN is metered, so Network Optimizer traced it once.");
    }

    [Fact]
    public void Finished_ShowsForADayAfterItCloses()
    {
        PathEpisodePresentation.Banner(new[] { Episode(1, Now.AddHours(-23.5), Now.AddHours(-23)) }, Now)
            .Should().NotBeNull();
        PathEpisodePresentation.Banner(new[] { Episode(1, Now.AddHours(-26), Now.AddHours(-25)) }, Now)
            .Should().BeNull();
    }

    [Fact]
    public void Finished_ConfirmedIsAWarningAndUnverifiableIsInfo()
    {
        var confirmed = PathEpisodePresentation.Banner(new[]
            { Episode(1, Now.AddHours(-2), Now.AddHours(-1), disposition: CongestionDisposition.Confirmed) }, Now)!;
        confirmed.CssClass.Should().Be("alert-warning");
        confirmed.Lead.Should().Be("Path degradation localized");
        confirmed.Text.Should().EndWith("Past the last clean hop.");

        var unverifiable = PathEpisodePresentation.Banner(new[] { Episode(1, Now.AddHours(-2), Now.AddHours(-1)) }, Now)!;
        unverifiable.CssClass.Should().Be("alert-info");
        unverifiable.Lead.Should().Be("Path trace finished");
    }

    [Fact]
    public void Symptom_NamesLossAndAddedLatency()
    {
        PathEpisodePresentation.Symptom(Episode(1, Now, null, peakLoss: 0, baseRtt: 40, peakRtt: 82))
            .Should().Be("running 42 ms slower");
        PathEpisodePresentation.Symptom(Episode(1, Now, null, peakLoss: 9, baseRtt: 40, peakRtt: 82))
            .Should().Be("losing packets (peak 9%) and running 42 ms slower");
    }

    [Fact]
    public void PastSymptom_NamesWhatHappenedAndForHowLong()
    {
        PathEpisodePresentation.PastSymptom(Episode(1, Now, Now, peakLoss: 45.7), "18 min")
            .Should().Be("lost packets for 18 min (peak 46%)");
        PathEpisodePresentation.PastSymptom(Episode(1, Now, Now, peakLoss: 0, baseRtt: 40, peakRtt: 82), "18 min")
            .Should().Be("ran 42 ms slower for 18 min");
    }

    [Fact]
    public void Together_SaysWhatTheTargetsDid()
    {
        PathEpisodePresentation.Together(new[] { ((double?)5, (double?)40, (double?)41) }).Should().Be("lost packets");
        PathEpisodePresentation.Together(new[] { ((double?)0, (double?)40, (double?)60) }).Should().Be("slowed down");
        PathEpisodePresentation.Together(new[] { ((double?)5, (double?)40, (double?)41), ((double?)0, (double?)40, (double?)60) })
            .Should().Be("lost packets and slowed down");
    }

    [Fact]
    public void DismissKey_IsPerSiteEpisodeAndPhase()
    {
        var b = PathEpisodePresentation.Banner(new[] { Episode(7, Now.AddHours(-1), null, tracing: true) }, Now)!;
        b.DismissKey("main").Should().Be("PathEpisodeBannerDismissed:main:7:tracing");
        b.DismissKey("other").Should().NotBe(b.DismissKey("main"));
    }
}
