using FluentAssertions;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// Window selection over the 168-bucket hour-of-week fingerprint. The interesting behavior is
/// where raw busy-ness and the sane-hours preference disagree, and at the seams: a window long
/// enough to run off the end of the week, and a day/hour that has already passed today.
/// </summary>
public class QuietWindowCalculatorTests
{
    private static readonly DateTime MondayNoon = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Unspecified);

    private static int Bucket(DayOfWeek day, int hour) => (int)day * 24 + hour;

    private static double[] Uniform(double value)
    {
        var buckets = new double[QuietWindowCalculator.BucketsPerWeek];
        Array.Fill(buckets, value);
        return buckets;
    }

    [Fact]
    public void Fixture_MondayNoon_IsAMonday()
    {
        MondayNoon.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void FindBest_PicksTheLowestMeanWindow()
    {
        var busy = Uniform(0.5);
        busy[Bucket(DayOfWeek.Wednesday, 3)] = 0.0;

        var proposal = QuietWindowCalculator.FindBest(busy, 3600, MondayNoon, TimeSpan.Zero);

        proposal.Day.Should().Be(DayOfWeek.Wednesday);
        proposal.Hour.Should().Be(3);
        proposal.BusyScore.Should().Be(0.0);
        proposal.UsedFallback.Should().BeFalse();
        proposal.Basis.Should().Be("7-day usage history");
    }

    [Fact]
    public void FindBest_DaytimePenalty_SteersTowardOvernightEvenWhenSlightlyBusier()
    {
        var busy = Uniform(1.0);
        busy[Bucket(DayOfWeek.Tuesday, 14)] = 0.0;
        busy[Bucket(DayOfWeek.Tuesday, 2)] = 0.1;

        var proposal = QuietWindowCalculator.FindBest(busy, 3600, MondayNoon, TimeSpan.Zero);

        proposal.Day.Should().Be(DayOfWeek.Tuesday);
        proposal.Hour.Should().Be(2);
        proposal.BusyScore.Should().BeApproximately(0.1, 1e-9);
    }

    [Fact]
    public void FindBest_AllBucketsIdle_TakesTheSoonestOvernightWindow()
    {
        var proposal = QuietWindowCalculator.FindBest(Uniform(0.0), 3600, MondayNoon, TimeSpan.Zero);

        proposal.Day.Should().Be(DayOfWeek.Monday);
        proposal.Hour.Should().Be(22);
        proposal.StartLocal.Should().Be(new DateTime(2026, 8, 17, 22, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public void FindBest_MultiHourWindow_ScoresEveryBucketItSpans()
    {
        var busy = Uniform(1.0);
        busy[Bucket(DayOfWeek.Thursday, 2)] = 0.0;
        busy[Bucket(DayOfWeek.Thursday, 3)] = 0.0;

        // Only two idle hours exist, so a three-hour rollout has to absorb a busy one either
        // way; the earlier of the two equally-scored windows wins.
        var proposal = QuietWindowCalculator.FindBest(busy, 3 * 3600, MondayNoon, TimeSpan.Zero);

        proposal.Day.Should().Be(DayOfWeek.Thursday);
        proposal.Hour.Should().Be(1);
        proposal.BusyScore.Should().BeApproximately(1.0 / 3.0, 1e-9);
    }

    [Fact]
    public void FindBest_MultiHourWindow_WrapsAroundTheEndOfTheWeek()
    {
        var busy = Uniform(1.0);
        busy[Bucket(DayOfWeek.Saturday, 23)] = 0.0;
        busy[Bucket(DayOfWeek.Sunday, 0)] = 0.0;
        busy[Bucket(DayOfWeek.Sunday, 1)] = 0.0;

        var proposal = QuietWindowCalculator.FindBest(busy, 3 * 3600, MondayNoon, TimeSpan.Zero);

        proposal.Day.Should().Be(DayOfWeek.Saturday);
        proposal.Hour.Should().Be(23);
        proposal.BusyScore.Should().Be(0.0);
    }

    [Fact]
    public void FindBest_SubHourDuration_StillScoresOneBucket()
    {
        var busy = Uniform(0.5);
        busy[Bucket(DayOfWeek.Friday, 4)] = 0.0;

        var proposal = QuietWindowCalculator.FindBest(busy, 60, MondayNoon, TimeSpan.Zero);

        proposal.Day.Should().Be(DayOfWeek.Friday);
        proposal.Hour.Should().Be(4);
    }

    [Fact]
    public void FindBest_ZeroDuration_StillScoresOneBucket()
    {
        var busy = Uniform(0.5);
        busy[Bucket(DayOfWeek.Friday, 4)] = 0.0;

        var proposal = QuietWindowCalculator.FindBest(busy, 0, MondayNoon, TimeSpan.Zero);

        proposal.Day.Should().Be(DayOfWeek.Friday);
        proposal.Hour.Should().Be(4);
    }

    [Fact]
    public void FindBest_RespectsMinimumLead()
    {
        var proposal = QuietWindowCalculator.FindBest(Uniform(0.0), 3600, MondayNoon, TimeSpan.FromHours(12));

        proposal.StartLocal.Should().BeOnOrAfter(MondayNoon.AddHours(12));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(167)]
    [InlineData(169)]
    [InlineData(0)]
    public void FindBest_WrongFingerprintLength_Throws(int length)
    {
        var act = () => QuietWindowCalculator.FindBest(new double[length], 3600, MondayNoon, TimeSpan.Zero);

        act.Should().Throw<ArgumentException>().WithParameterName("busy168");
    }

    [Fact]
    public void Fallback_HomeProfile_IsWeekdayOvernight()
    {
        var proposal = QuietWindowCalculator.Fallback(SiteUsageProfile.Home, MondayNoon, TimeSpan.Zero);

        proposal.Day.Should().Be(DayOfWeek.Tuesday);
        proposal.Hour.Should().Be(3);
        proposal.UsedFallback.Should().BeTrue();
        proposal.BusyScore.Should().Be(0);
        proposal.Basis.Should().Contain("home-profile");
        proposal.StartLocal.Should().Be(new DateTime(2026, 8, 18, 3, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public void Fallback_BusinessProfile_IsWeekendEarlyMorning()
    {
        var proposal = QuietWindowCalculator.Fallback(SiteUsageProfile.Business, MondayNoon, TimeSpan.Zero);

        proposal.Day.Should().Be(DayOfWeek.Sunday);
        proposal.Hour.Should().Be(4);
        proposal.UsedFallback.Should().BeTrue();
        proposal.Basis.Should().Contain("business-profile");
        proposal.StartLocal.Should().Be(new DateTime(2026, 8, 23, 4, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public void Fixed_UsesThePinnedDayAndHour()
    {
        var proposal = QuietWindowCalculator.Fixed(DayOfWeek.Saturday, 1, MondayNoon, TimeSpan.Zero);

        proposal.Day.Should().Be(DayOfWeek.Saturday);
        proposal.Hour.Should().Be(1);
        proposal.UsedFallback.Should().BeFalse();
        proposal.Basis.Should().Be("pinned day and hour");
        proposal.StartLocal.Should().Be(new DateTime(2026, 8, 22, 1, 0, 0, DateTimeKind.Unspecified));
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(25, 23)]
    public void Fixed_ClampsTheHourIntoRange(int hour, int expected)
    {
        var proposal = QuietWindowCalculator.Fixed(DayOfWeek.Saturday, hour, MondayNoon, TimeSpan.Zero);

        proposal.Hour.Should().Be(expected);
        proposal.StartLocal.Hour.Should().Be(expected);
    }

    [Fact]
    public void NextOccurrence_LaterToday_StaysToday()
    {
        var next = QuietWindowCalculator.NextOccurrence(DayOfWeek.Monday, 22, MondayNoon, TimeSpan.Zero);

        next.Should().Be(new DateTime(2026, 8, 17, 22, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public void NextOccurrence_AlreadyPassedToday_RollsToNextWeek()
    {
        var next = QuietWindowCalculator.NextOccurrence(DayOfWeek.Monday, 3, MondayNoon, TimeSpan.Zero);

        next.Should().Be(new DateTime(2026, 8, 24, 3, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public void NextOccurrence_MinLeadPushesPastTheWindow_RollsToNextWeek()
    {
        var next = QuietWindowCalculator.NextOccurrence(DayOfWeek.Monday, 13, MondayNoon, TimeSpan.FromHours(4));

        next.Should().Be(new DateTime(2026, 8, 24, 13, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public void NextOccurrence_MinLeadCanCarryIntoTheFollowingDay()
    {
        var next = QuietWindowCalculator.NextOccurrence(DayOfWeek.Wednesday, 3, MondayNoon, TimeSpan.FromHours(20));

        next.Should().Be(new DateTime(2026, 8, 19, 3, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public void NextOccurrence_PreservesTheDateTimeKind()
    {
        var nowLocal = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Local);

        var next = QuietWindowCalculator.NextOccurrence(DayOfWeek.Tuesday, 3, nowLocal, TimeSpan.Zero);

        next.Kind.Should().Be(DateTimeKind.Local);
    }
}

/// <summary>
/// The fleet-shape heuristic that stands in for usage history on a brand-new site. Each
/// threshold gets its own case because they are independent doors into Business.
/// </summary>
public class SiteProfileClassifierTests
{
    [Fact]
    public void Classify_LargeInfrastructureCount_IsBusiness()
    {
        SiteProfileClassifier.Classify(infraDeviceCount: 12, apCount: 1, switchCount: 1, clientCount: 5)
            .Should().Be(SiteUsageProfile.Business);
    }

    [Fact]
    public void Classify_LargeClientCount_IsBusiness()
    {
        SiteProfileClassifier.Classify(infraDeviceCount: 3, apCount: 1, switchCount: 1, clientCount: 40)
            .Should().Be(SiteUsageProfile.Business);
    }

    [Fact]
    public void Classify_MultiApMultiSwitchFleet_IsBusiness()
    {
        SiteProfileClassifier.Classify(infraDeviceCount: 7, apCount: 4, switchCount: 2, clientCount: 10)
            .Should().Be(SiteUsageProfile.Business);
    }

    [Fact]
    public void Classify_ManyApsButOneSwitch_IsHome()
    {
        SiteProfileClassifier.Classify(infraDeviceCount: 6, apCount: 5, switchCount: 1, clientCount: 20)
            .Should().Be(SiteUsageProfile.Home);
    }

    [Fact]
    public void Classify_ManySwitchesButFewAps_IsHome()
    {
        SiteProfileClassifier.Classify(infraDeviceCount: 8, apCount: 3, switchCount: 4, clientCount: 39)
            .Should().Be(SiteUsageProfile.Home);
    }

    [Fact]
    public void Classify_JustBelowEveryThreshold_IsHome()
    {
        SiteProfileClassifier.Classify(infraDeviceCount: 11, apCount: 3, switchCount: 1, clientCount: 39)
            .Should().Be(SiteUsageProfile.Home);
    }

    [Fact]
    public void Classify_EmptySite_IsHome()
    {
        SiteProfileClassifier.Classify(0, 0, 0, 0).Should().Be(SiteUsageProfile.Home);
    }
}
