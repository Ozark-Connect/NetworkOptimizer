using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// "Has the elevation stopped" rather than "what is the median", because the noise floor
/// downstream keeps only the elevated samples - so the reported figure is the median of the bad
/// ones, and comparing medians cannot see a fix at all.
/// <para>
/// The bar for calling it over: a run of clean load episodes, and - where the history shows the
/// problem was tied to a time of day - one of them at that hour. Every case here came from being
/// wrong about a real WAN first.
/// </para>
/// </summary>
public class ElevationVerdictTests
{
    private const double NoiseFloor = 0.5;
    private const double HourDependenceFloor = 3.0;
    private const int StaleEpisodes = 3;
    private static readonly TimeSpan EpisodeSpan = TimeSpan.FromSeconds(7);

    // Local time, because the rule reasons about the operator's hours.
    private static DateTime At(int dayOffset, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 8, 5, hour, minute, 0, DateTimeKind.Unspecified).AddDays(-dayOffset),
            TimeZoneInfo.Local);

    private static ElevationVerdict.Verdict Judge(
        params (DateTime Time, double Value)[] newestFirst)
        => ElevationVerdict.For(newestFirst, NoiseFloor, StaleEpisodes, true, EpisodeSpan, HourDependenceFloor);

    [Fact]
    public void A_line_still_misbehaving_is_not_over()
    {
        // Newest episodes are elevated: nothing to declare.
        var verdict = Judge(
            (At(0, 22), 23), (At(0, 21), 0), (At(0, 20), 0), (At(0, 8), 24), (At(1, 8), 23));

        verdict.ElevationIsOver.Should().BeFalse();
        verdict.CleanRun.Should().BeEmpty();
    }

    [Fact]
    public void A_line_that_was_never_elevated_has_no_elevation_to_declare_over()
    {
        // Not "cleared" of a problem it never had - but the caller reads ElevatedCount 0 as its own
        // answer: every load episode was clean, which is the strongest statement available and the
        // reason this line no longer falls through to the median of whichever samples crossed the
        // noise floor. That path reported 23 ms on a WAN whose every episode read under 0.5.
        var verdict = Judge((At(0, 22), 0.1), (At(0, 21), 0), (At(0, 20), 0.2), (At(1, 8), 0.1));

        verdict.ElevatedCount.Should().Be(0);
        verdict.ElevationIsOver.Should().BeFalse();
    }

    [Fact]
    public void A_constant_problem_clears_from_any_hour()
    {
        // The WAN4 case. Elevated in EVERY episode before the fix, so the hour was never the
        // variable - the line misbehaved whenever it was loaded. Three clean saturations at 22:00
        // disprove it without waiting for the 08:00 scheduled test to come round again.
        var verdict = Judge(
            (At(0, 22, 30), 0.0), (At(0, 22), 0.1), (At(0, 21, 55), 0.0),
            (At(0, 20, 37), 23.9), (At(0, 8), 24.4), (At(1, 8), 23.1), (At(2, 8), 24.0));

        verdict.ProblemHourReTested.Should().BeTrue();
        verdict.ElevationIsOver.Should().BeTrue();
    }

    [Fact]
    public void A_nightly_problem_does_not_clear_itself_at_3am()
    {
        // Bad every evening, clean every night. A run computed at 3 AM sees three clean episodes on
        // top of elevated ones - and must NOT call that fixed.
        var verdict = Judge(
            (At(0, 3), 0.0), (At(0, 2), 0.1), (At(0, 1), 0.0),
            (At(1, 20), 22.0), (At(1, 14), 0.2), (At(2, 20), 21.0), (At(2, 14), 0.1));

        verdict.CleanRun.Should().HaveCount(3);
        verdict.ProblemHourReTested.Should().BeFalse();
        verdict.ElevationIsOver.Should().BeFalse();
    }

    [Fact]
    public void A_nightly_problem_clears_once_its_own_hour_comes_back_clean()
    {
        // Same line, but the evening has now been re-tested and was fine.
        var verdict = Judge(
            (At(0, 20), 0.1), (At(0, 19), 0.0), (At(0, 14), 0.0),
            (At(1, 20), 22.0), (At(1, 14), 0.2), (At(2, 20), 21.0));

        verdict.ProblemHourReTested.Should().BeTrue();
        verdict.ElevationIsOver.Should().BeTrue();
    }

    [Fact]
    public void A_short_clean_run_is_not_enough()
    {
        var verdict = Judge((At(0, 22), 0.0), (At(0, 21), 0.1), (At(0, 8), 24.0), (At(1, 8), 23.0));

        verdict.CleanRun.Should().HaveCount(2);
        verdict.ElevationIsOver.Should().BeFalse();
    }

    [Fact]
    public void The_hour_rule_can_be_turned_off()
    {
        var episodes = new[]
        {
            (At(0, 3), 0.0), (At(0, 2), 0.1), (At(0, 1), 0.0),
            (At(1, 20), 22.0), (At(1, 14), 0.2), (At(2, 20), 21.0),
        };

        ElevationVerdict.For(episodes, NoiseFloor, StaleEpisodes, false, EpisodeSpan, HourDependenceFloor)
            .ElevationIsOver.Should().BeTrue();
    }
}
