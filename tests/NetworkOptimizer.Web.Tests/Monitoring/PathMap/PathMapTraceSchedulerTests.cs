using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.PathMap;
using Xunit;
using Candidate = NetworkOptimizer.Web.Services.Monitoring.PathMap.PathMapTraceScheduler.Candidate;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathMap;

public class PathMapTraceSchedulerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<int> None = [];

    private static Candidate C(int id, bool viewed = true, bool metered = false, bool urgent = false, bool amber = false,
        double? ageSeconds = null) =>
        new(id, $"t{id}", viewed, metered, urgent, amber, ageSeconds is double s ? Now.AddSeconds(-s) : null);

    [Fact]
    public void Pick_Viewed_SkipsARouteUnderAMinuteOld()
    {
        PathMapTraceScheduler.Pick([C(1, ageSeconds: 30)], None, Now).Should().BeNull();
        PathMapTraceScheduler.Pick([C(1, ageSeconds: 61)], None, Now)!.Id.Should().Be(1);
    }

    [Fact]
    public void Pick_Viewed_AmberGoesFirst()
    {
        PathMapTraceScheduler.Pick([C(1, ageSeconds: 600), C(2, amber: true, ageSeconds: 70)], None, Now)!.Id.Should().Be(2);
    }

    [Fact]
    public void Pick_Viewed_OldestRouteFirst_NeverTracedOldest()
    {
        PathMapTraceScheduler.Pick([C(1, ageSeconds: 90), C(2), C(3, ageSeconds: 300)], None, Now)!.Id.Should().Be(2);
    }

    [Fact]
    public void Pick_MeteredViewed_TracesOncePerViewing_AndSkipsAFreshRoute()
    {
        PathMapTraceScheduler.Pick([C(1, metered: true, ageSeconds: 600)], None, Now).Should().BeNull();
        PathMapTraceScheduler.Pick([C(1, metered: true, ageSeconds: 1000)], None, Now)!.Id.Should().Be(1);
        PathMapTraceScheduler.Pick([C(1, metered: true, ageSeconds: 1000)], new HashSet<int> { 1 }, Now).Should().BeNull();
    }

    [Fact]
    public void Pick_NoViewer_TracesHourly_AndADegradedTargetAtOnce()
    {
        PathMapTraceScheduler.Pick([C(1, viewed: false, ageSeconds: 1800)], None, Now).Should().BeNull();
        PathMapTraceScheduler.Pick([C(1, viewed: false, ageSeconds: 3700)], None, Now)!.Id.Should().Be(1);
        PathMapTraceScheduler.Pick([C(1, viewed: false, urgent: true, ageSeconds: 30)], None, Now)!.Id.Should().Be(1);
    }

    [Fact]
    public void Pick_MeteredWithNoViewer_NeverTraces()
    {
        PathMapTraceScheduler.Pick([C(1, viewed: false, metered: true, urgent: true, ageSeconds: 99999)], None, Now)
            .Should().BeNull();
    }

    [Fact]
    public void Selection_KeyIsStableAcrossOrder()
    {
        PathMapSelection.Parse("wan2,WAN", false, "wan").Key.Should().Be(PathMapSelection.Parse("wan, wan2", false, "wan").Key);
        PathMapSelection.Parse(null, false, "wan").Wans.Should().Equal("wan");
    }
}
