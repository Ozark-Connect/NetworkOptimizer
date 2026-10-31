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
    public void Tracing_WinsOverANewerFinishedEpisode()
    {
        var b = PathEpisodePresentation.Banner(new[]
        {
            Episode(1, Now.AddHours(-1), null, tracing: true),
            Episode(2, Now.AddMinutes(-30), Now.AddMinutes(-10))
        }, Now);

        b!.EpisodeId.Should().Be(1);
        b.Phase.Should().Be("tracing");
        b.Lead.Should().Be("Tracing now:");
        b.Text.Should().StartWith("Target A is losing packets (peak 27%) while the rest");
        b.More.Should().Be(1);
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
        confirmed.Lead.Should().Be("Path degradation localized:");
        confirmed.Text.Should().EndWith("Past the last clean hop.");

        var unverifiable = PathEpisodePresentation.Banner(new[] { Episode(1, Now.AddHours(-2), Now.AddHours(-1)) }, Now)!;
        unverifiable.CssClass.Should().Be("alert-info");
        unverifiable.Lead.Should().Be("Path trace finished:");
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
    public void DismissKey_IsPerSiteEpisodeAndPhase()
    {
        var b = PathEpisodePresentation.Banner(new[] { Episode(7, Now.AddHours(-1), null, tracing: true) }, Now)!;
        b.DismissKey("main").Should().Be("PathEpisodeBannerDismissed:main:7:tracing");
        b.DismissKey("other").Should().NotBe(b.DismissKey("main"));
    }
}
