using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
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

    private static List<IspHealthIssue> RecurringFindings(IEnumerable<PathEpisodeView> episodes, IReadOnlyDictionary<int, int>? points = null) =>
        PathEpisodePresentation.Findings(episodes.ToList(), new IspHealthOptions(), TimeZoneInfo.Utc, DisplayFormat.Fallback, points).ToList();

    private static IEnumerable<PathEpisodeView> OnDays(params int[] days) =>
        days.Select(d => Episode(d, Now.AddDays(-d), Now.AddDays(-d).AddMinutes(30)));

    [Fact]
    public void Recurring_finding_carries_a_stable_ack_key()
    {
        var first = RecurringFindings(OnDays(1, 2, 3)).Single();
        var later = RecurringFindings(OnDays(1, 2, 3, 4)).Single();

        first.AckKey.Should().StartWith("path-recurring:");
        later.AckKey.Should().Be(first.AckKey, "another occurrence of the same finding keeps its acknowledgment");
    }

    [Fact]
    public void Recurring_finding_that_costs_points_cannot_be_acknowledged()
    {
        var finding = RecurringFindings(OnDays(1, 2, 3), new Dictionary<int, int> { [1] = 2 }).Single();

        finding.AckKey.Should().BeNull();
    }

    [Fact]
    public void Another_target_joining_is_a_new_finding()
    {
        var alone = RecurringFindings(OnDays(1, 2, 3)).Single();
        var joined = RecurringFindings(OnDays(1, 2, 3).Select(e => e with
        {
            Degraded = e.Degraded.Append(new PathEpisodeTargetView(2, "t2", "Target B", PathEpisodeTargetRole.Degraded, 12, 13, 0, 27, null)).ToArray()
        })).Single();

        joined.AckKey.Should().NotBe(alone.AckKey);
    }

    private static PathEpisodeTargetView Target(string id, int? asn) =>
        new(1, id, id, PathEpisodeTargetRole.Degraded, 12, 13, 0, 27, null, AsnNumber: asn);

    [Fact]
    public void Several_targets_in_one_network_can_be_acknowledged()
    {
        var finding = RecurringFindings(OnDays(1, 2, 3).Select(e => e with { Degraded = new[] { Target("a", 16509), Target("b", 16509) } })).Single();

        finding.AckKey.Should().NotBeNull();
    }

    [Fact]
    public void Targets_in_several_networks_are_never_acknowledgeable()
    {
        var finding = RecurringFindings(OnDays(1, 2, 3).Select(e => e with { Degraded = new[] { Target("a", 16509), Target("b", 714) } })).Single();

        finding.AckKey.Should().BeNull("degradation across networks points at something they share");
    }

    [Fact]
    public void Acknowledged_episodes_are_the_ones_of_an_acknowledged_finding()
    {
        var episodes = OnDays(1, 2, 3).ToList();
        var key = RecurringFindings(episodes).Single().AckKey!;

        PathEpisodePresentation.AcknowledgedEpisodeIds(episodes, new IspHealthOptions(), null, new HashSet<string> { key })
            .Should().BeEquivalentTo(new[] { 1, 2, 3 });
        PathEpisodePresentation.AcknowledgedEpisodeIds(episodes, new IspHealthOptions(), null, new HashSet<string>())
            .Should().BeEmpty();
    }

    [Fact]
    public void An_episode_carries_its_recurring_findings_key()
    {
        var episodes = OnDays(1, 2, 3).ToList();

        PathEpisodePresentation.AckKeyFor(episodes[0]).Should().Be(RecurringFindings(episodes).Single().AckKey);
    }

    [Fact]
    public void Confirmed_finding_is_never_acknowledgeable()
    {
        var confirmed = RecurringFindings(OnDays(1, 2, 3).Select(e => e with { Disposition = CongestionDisposition.Confirmed })).Single();

        confirmed.AckKey.Should().BeNull();
    }

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

        // A Custom target opens on the episode's WAN, which its trace went out over; an open episode's window ends now.
        var open = Episode(2, Now.AddMinutes(-10), null, tracing: true);
        var url = PathEpisodePresentation.TargetUrl(open, transit with { Category = "Custom" }, "wan2", Now);
        url.Should().Contain("category=Custom").And.Contain("&wan=wan2&").And.Contain($"&span={(long)TimeSpan.FromMinutes(40).TotalMilliseconds}");

        // A Fabric target is LAN, so it asks for every WAN.
        PathEpisodePresentation.TargetUrl(open, transit with { Category = "Fabric" }, "wan2", Now).Should().Contain("&wan=all");
    }

    [Fact]
    public void Tracing_WinsOverANewerFinishedEpisode()
    {
        var b = PathEpisodePresentation.Banner(new[]
        {
            Episode(1, Now.AddHours(-1), null, tracing: true),
            Episode(2, Now.AddMinutes(-30), Now.AddMinutes(-10))
        }, Now, TimeZoneInfo.Utc, DisplayFormat.Fallback);

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

        var b = PathEpisodePresentation.Banner(new[] { e, Episode(4, Now.AddMinutes(-30), Now.AddMinutes(-10)) }, Now, TimeZoneInfo.Utc, DisplayFormat.Fallback)!;
        b.EpisodeId.Should().Be(3);
        b.Phase.Should().Be("ongoing");
        b.Lead.Should().Be("Still slow");
        b.Text.Should().Be("Target A is still running 21 ms slower. Network Optimizer traced it 10 times, and traces it once more after it recovers, to compare.");
        PathEpisodePresentation.BadgeLabel(e).Should().Be("Ongoing");
        PathEpisodePresentation.Entries(new[] { e }).Single().Text.Should().StartWith("Target A is running 21 ms slower, for ").And.Contain(" so far.");

        PathEpisodePresentation.Banner(new[] { e with { Metered = true } }, Now, TimeZoneInfo.Utc, DisplayFormat.Fallback)!.Text
            .Should().Be("Target A is still running 21 ms slower. This WAN is metered, so Network Optimizer traced it once.");
    }

    [Fact]
    public void Finished_ShowsForADayAfterItCloses()
    {
        PathEpisodePresentation.Banner(new[] { Episode(1, Now.AddHours(-23.5), Now.AddHours(-23)) }, Now, TimeZoneInfo.Utc, DisplayFormat.Fallback)
            .Should().NotBeNull();
        PathEpisodePresentation.Banner(new[] { Episode(1, Now.AddHours(-26), Now.AddHours(-25)) }, Now, TimeZoneInfo.Utc, DisplayFormat.Fallback)
            .Should().BeNull();
    }

    [Fact]
    public void Finished_ConfirmedIsAWarningAndUnverifiableIsInfo()
    {
        var confirmed = PathEpisodePresentation.Banner(new[]
            { Episode(1, Now.AddHours(-2), Now.AddHours(-1), disposition: CongestionDisposition.Confirmed) }, Now, TimeZoneInfo.Utc, DisplayFormat.Fallback)!;
        confirmed.CssClass.Should().Be("alert-warning");
        confirmed.Lead.Should().Be("Path degradation localized");
        confirmed.Text.Should().EndWith("Past the last clean hop.");

        var unverifiable = PathEpisodePresentation.Banner(new[] { Episode(1, Now.AddHours(-2), Now.AddHours(-1)) }, Now, TimeZoneInfo.Utc, DisplayFormat.Fallback)!;
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
        var b = PathEpisodePresentation.Banner(new[] { Episode(7, Now.AddHours(-1), null, tracing: true) }, Now, TimeZoneInfo.Utc, DisplayFormat.Fallback)!;
        b.DismissKey("main").Should().Be("PathEpisodeBannerDismissed:main:7:tracing");
        b.DismissKey("other").Should().NotBe(b.DismissKey("main"));
    }
}
