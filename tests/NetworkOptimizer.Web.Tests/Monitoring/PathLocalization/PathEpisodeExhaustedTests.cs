using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathLocalization;

/// <summary>
/// An episode that hits its time limit blocks a second one on the same stuck level until the target
/// runs clean, and the block survives a restart.
/// </summary>
public class PathEpisodeExhaustedTests
{
    private static readonly IspHealthOptions Options = new();
    private static readonly DateTime T0 = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
    private const string TimedOutSummary = "Every router answered cleanly. Still degraded after 6 " + PathEpisodeWatcher.TimeLimitNoteTail;

    private static DateTime Feed(TargetDegradation t, DateTime from, TimeSpan span, double rtt)
    {
        var at = from;
        for (; at < from + span; at += TimeSpan.FromSeconds(15))
            t.Record(at, rtt, 5, 5, () => false);
        return at;
    }

    [Fact]
    public void StuckLevel_StaysExhausted_UntilATargetRunsClean()
    {
        var t = new TargetDegradation("x", Options);
        var now = Feed(t, T0, TimeSpan.FromHours(6), 2);
        now = Feed(t, now, TimeSpan.FromHours(1), 18);
        t.Degraded.Should().BeTrue();
        var stoppedAt = now;

        now = Feed(t, now, TimeSpan.FromHours(1), 18);
        PathEpisodeWatcher.IsExhausted(stoppedAt, t).Should().BeTrue();

        // Recovers, then degrades again: a new degradation, which may open its own episode.
        now = Feed(t, now, TimeSpan.FromMinutes(20), 2);
        Feed(t, now, TimeSpan.FromMinutes(20), 18);
        t.Degraded.Should().BeTrue();
        PathEpisodeWatcher.IsExhausted(stoppedAt, t).Should().BeFalse();
    }

    [Fact]
    public void AfterARestart_AReseededTrackerAtTheStuckLevel_StaysExhausted()
    {
        // The reseeded tracker judges the stuck level against the old baseline and degrades again with a
        // new DegradedSince; it has never run clean since the stop, so the block holds.
        var stoppedAt = T0.AddHours(12);
        var t = new TargetDegradation("x", Options);
        t.Seed(Enumerable.Range(0, 144).Select(i => (stoppedAt.AddMinutes(-720 + 5 * i), (double?)2.0, (double?)0.0)),
            Array.Empty<(DateTime, DateTime)>());
        Feed(t, stoppedAt.AddMinutes(50), TimeSpan.FromMinutes(20), 18);

        t.Degraded.Should().BeTrue();
        t.DegradedSince.Should().BeAfter(stoppedAt);
        PathEpisodeWatcher.IsExhausted(stoppedAt, t).Should().BeTrue();
    }

    private static PathEpisode Episode(int id, DateTime opened, DateTime? closed, string? summary, params int[] degradedTargetIds) => new()
    {
        Id = id,
        OpenedAt = opened,
        ClosedAt = closed,
        Summary = summary,
        Targets = degradedTargetIds.Select(t => new PathEpisodeTarget { MonitoringTargetId = t, Role = PathEpisodeTargetRole.Degraded }).ToList()
    };

    [Fact]
    public void TimedOutTargets_TakesTheNewestEpisodePerTarget()
    {
        var episodes = new[]
        {
            Episode(1, T0, T0.AddHours(6), TimedOutSummary, 10, 11),
            // Target 11 has a newer episode that cleared: its block was already lifted.
            Episode(2, T0.AddHours(8), T0.AddHours(9), "Every router answered cleanly.", 11),
            // Target 12's newest is still open.
            Episode(3, T0, T0.AddHours(6), TimedOutSummary, 12),
            Episode(4, T0.AddHours(7), null, "Tracing now.", 12),
        };

        var stopped = PathEpisodeWatcher.TimedOutTargets(episodes);

        stopped.Keys.Should().BeEquivalentTo(new[] { 10 });
        stopped[10].Should().Be(T0.AddHours(6));
        stopped[10].Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void TimedOutTargets_IgnoresControls()
    {
        var episode = Episode(1, T0, T0.AddHours(6), TimedOutSummary, 10);
        episode.Targets.Add(new PathEpisodeTarget { MonitoringTargetId = 20, Role = PathEpisodeTargetRole.Control });

        PathEpisodeWatcher.TimedOutTargets(new[] { episode }).Keys.Should().BeEquivalentTo(new[] { 10 });
    }
}
