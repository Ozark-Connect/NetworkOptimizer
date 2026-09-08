using FluentAssertions;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

public class CongestionProfileLearnerTests
{
    private static readonly DateTime Start = new(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc); // a Monday

    /// <summary>A full week of hourly samples following a daily curve: fast overnight, a dip in the evening.</summary>
    private static List<LearningSample> Week(Func<int, int, double> down, Func<int, int, double>? up = null)
    {
        var samples = new List<LearningSample>();
        long id = 1;
        for (var day = 0; day < 7; day++)
            for (var hour = 0; hour < 24; hour++)
                samples.Add(new LearningSample(id++, day, hour, down(day, hour), (up ?? ((d, h) => down(d, h) / 10))(day, hour), Start.AddDays(day).AddHours(hour)));
        return samples;
    }

    private static double EveningDip(int day, int hour) => hour is >= 18 and <= 22 ? 100 : 200;

    [Fact]
    public void FullWeek_IsReliable_AndBestHourIsOne()
    {
        var result = CongestionProfileLearner.Learn(Week(EveningDip));
        var p = result.Profile;

        p.ValidSampleCount.Should().Be(168);
        p.Coverage.Should().Be(1.0);
        p.DaysSpanned.Should().Be(7);
        p.IsReliable.Should().BeTrue();
        p.DownloadMultipliers.Max().Should().Be(1.0);
        p.UploadMultipliers.Max().Should().Be(1.0);
        p.PeakDownloadMbps.Should().BeApproximately(200, 1);
        result.Exclusions.Should().BeEmpty();
    }

    [Fact]
    public void RealEveningDip_SurvivesSmoothing()
    {
        var p = CongestionProfileLearner.Learn(Week(EveningDip)).Profile;

        // The heart of the dip (20:00) stays near half; the shoulders blend, but the dip is unmistakable.
        p.DownloadMultipliers[LearnedCongestionProfile.SlotIndex(2, 20)].Should().BeLessThan(0.6);
        p.DownloadMultipliers[LearnedCongestionProfile.SlotIndex(2, 3)].Should().BeGreaterThan(0.95);
    }

    [Fact]
    public void OneErrantLowSample_IsExcluded_AndDoesNotCarveAHole()
    {
        var samples = Week(EveningDip);
        // Tuesday 03:00 measured while something was downloading: a tenth of the usual.
        var slot = samples.First(s => s.DayOfWeek == 1 && s.Hour == 3);
        samples[samples.IndexOf(slot)] = slot with { DownloadMbps = 20 };

        var result = CongestionProfileLearner.Learn(samples);

        result.Exclusions.Should().ContainSingle(e => e.SampleId == slot.Id && e.Reason.Contains("download"));
        result.Profile.DownloadMultipliers[LearnedCongestionProfile.SlotIndex(1, 3)].Should().BeGreaterThan(0.9);
        result.Profile.SampleCounts[LearnedCongestionProfile.SlotIndex(1, 3)].Should().Be(0);
    }

    [Fact]
    public void OneErrantHighSample_DoesNotSetThePeak()
    {
        var samples = Week(EveningDip);
        var slot = samples.First(s => s.DayOfWeek == 4 && s.Hour == 2);
        samples[samples.IndexOf(slot)] = slot with { DownloadMbps = 900 };

        var result = CongestionProfileLearner.Learn(samples);

        result.Exclusions.Should().ContainSingle(e => e.SampleId == slot.Id);
        result.Profile.PeakDownloadMbps.Should().BeLessThan(220);
    }

    [Fact]
    public void ErrantUpload_ExcludesTheWholeSample()
    {
        var samples = Week(EveningDip);
        var slot = samples.First(s => s.DayOfWeek == 3 && s.Hour == 10);
        samples[samples.IndexOf(slot)] = slot with { UploadMbps = 1 };

        var result = CongestionProfileLearner.Learn(samples);

        result.Exclusions.Should().ContainSingle(e => e.SampleId == slot.Id && e.Reason.Contains("upload"));
        result.Profile.ValidSampleCount.Should().Be(167);
    }

    [Fact]
    public void SingleEventOnOneEvening_IsDilutedByTheOtherSix()
    {
        // Sunday 20:00-21:00 collapses to a quarter (a one-off), the other evenings hold the usual dip.
        var samples = Week((d, h) => d == 6 && h is 20 or 21 ? 25 : EveningDip(d, h));

        var p = CongestionProfileLearner.Learn(samples).Profile;

        // Pool median at 20:00 is 100; 25 is below 45% of it, so the event is rejected outright,
        // and Sunday 20:00 smooths back toward the ordinary evening rather than to a quarter.
        p.DownloadMultipliers[LearnedCongestionProfile.SlotIndex(6, 20)].Should().BeGreaterThan(0.4);
    }

    [Fact]
    public void FewSamples_AreNotReliable_ButStillYieldACurve()
    {
        var samples = Week(EveningDip).Where(s => s.DayOfWeek < 2).ToList();

        var p = CongestionProfileLearner.Learn(samples).Profile;

        p.IsReliable.Should().BeFalse();
        p.ValidSampleCount.Should().Be(48);
        p.Coverage.Should().BeApproximately(48 / 168.0, 0.001);
        // Empty days borrow the hour-of-day shape rather than reading as flat.
        p.DownloadMultipliers[LearnedCongestionProfile.SlotIndex(5, 20)].Should().BeLessThan(
            p.DownloadMultipliers[LearnedCongestionProfile.SlotIndex(5, 3)]);
    }

    [Fact]
    public void OutlierRejection_WaitsForEnoughReadingsInTheHour()
    {
        // Three readings at 09:00: two normal, one low. Too few to judge, so all three are kept.
        var samples = new List<LearningSample>
        {
            new(1, 0, 9, 200, 20, Start),
            new(2, 1, 9, 200, 20, Start.AddDays(1)),
            new(3, 2, 9, 40, 20, Start.AddDays(2)),
        };

        var result = CongestionProfileLearner.Learn(samples);

        result.Exclusions.Should().BeEmpty();
        result.Profile.ValidSampleCount.Should().Be(3);
    }

    [Fact]
    public void NoThroughput_AndBadSlots_AreExcludedWithReasons()
    {
        var samples = new List<LearningSample>
        {
            new(1, 0, 9, 0, 20, Start),
            new(2, 7, 9, 200, 20, Start),
            new(3, 1, 24, 200, 20, Start),
            new(4, 1, 10, 200, 20, Start),
        };

        var result = CongestionProfileLearner.Learn(samples);

        result.Exclusions.Select(e => e.SampleId).Should().BeEquivalentTo(new long[] { 1, 2, 3 });
        result.Profile.ValidSampleCount.Should().Be(1);
    }

    [Fact]
    public void ProbeLimitedSamples_ShapeTheCurve_ButNeverSetThePeak()
    {
        // Overnight samples clipped at a 250 lift; the rest measured the line at 200 / 100.
        var samples = Week(EveningDip)
            .Select(s => s.Hour is >= 1 and <= 4 ? s with { DownloadMbps = 250, ProbeLimited = true } : s)
            .ToList();

        var p = CongestionProfileLearner.Learn(samples).Profile;

        p.ProbeLimitedSampleCount.Should().Be(28);
        p.PeakIsLowerBound.Should().BeFalse();
        // The peak is the best unclipped hour, not the lift.
        p.PeakDownloadMbps.Should().BeLessThan(215);
        // Clipped hours still read as at least as fast as the peak.
        p.DownloadMultipliers[LearnedCongestionProfile.SlotIndex(2, 2)].Should().Be(1.0);
    }

    [Fact]
    public void AllSamplesProbeLimited_MarksThePeakAsALowerBound()
    {
        var samples = Week(EveningDip).Select(s => s with { ProbeLimited = true }).ToList();

        var p = CongestionProfileLearner.Learn(samples).Profile;

        p.PeakIsLowerBound.Should().BeTrue();
        p.ProbeLimitedSampleCount.Should().Be(168);
        p.PeakDownloadMbps.Should().BeApproximately(200, 1);
    }

    [Fact]
    public void EmptyInput_YieldsFlatUnreliableProfile()
    {
        var p = CongestionProfileLearner.Learn(Array.Empty<LearningSample>()).Profile;

        p.IsReliable.Should().BeFalse();
        p.ValidSampleCount.Should().Be(0);
        p.DownloadMultipliers.Should().AllBeEquivalentTo(1.0);
    }

    [Fact]
    public void MultipliersNeverFallBelowTheFloor()
    {
        var samples = Week((d, h) => h == 20 ? 2 : 200);

        var p = CongestionProfileLearner.Learn(samples).Profile;

        p.DownloadMultipliers.Min().Should().BeGreaterThanOrEqualTo(CongestionProfileLearner.MinMultiplier);
    }

    [Fact]
    public void Learning_IsDeterministic()
    {
        var samples = Week(EveningDip);
        var a = CongestionProfileLearner.Learn(samples).Profile;
        var b = CongestionProfileLearner.Learn(samples).Profile;

        a.DownloadMultipliers.Should().Equal(b.DownloadMultipliers);
        a.UploadMultipliers.Should().Equal(b.UploadMultipliers);
    }

    [Fact]
    public void ScaledPattern_ReproducesLearnedThroughput_UnderNominal()
    {
        var multipliers = Enumerable.Repeat(0.5, LearnedCongestionProfile.Slots).ToArray();
        multipliers[0] = 1.0;

        // Nominal equals the peak: identity.
        var same = LearnedCongestionProfile.ScaledPattern(multipliers, 200, 200);
        same[0, 0].Should().Be(1.0);
        same[0, 1].Should().Be(0.5);

        // Nominal set lower than the peak: the best hour caps at nominal, the dip keeps its absolute value.
        var lower = LearnedCongestionProfile.ScaledPattern(multipliers, 200, 150);
        lower[0, 0].Should().Be(1.0);
        lower[0, 1].Should().BeApproximately(100.0 / 150.0, 0.0001);
    }

    [Fact]
    public void HourOfDayAverages_AverageAcrossTheWeek()
    {
        var multipliers = new double[LearnedCongestionProfile.Slots];
        for (var day = 0; day < 7; day++)
            for (var hour = 0; hour < 24; hour++)
                multipliers[LearnedCongestionProfile.SlotIndex(day, hour)] = hour == 20 ? 0.5 : 1.0;

        var avg = LearnedCongestionProfile.HourOfDayAverages(multipliers);

        avg[20].Should().Be(0.5);
        avg[3].Should().Be(1.0);
    }

    [Fact]
    public void DaysSpanned_CountsGatewayLocalDays_NotUtcDates()
    {
        // Nine hours of samples on one local afternoon and evening, stored in UTC so the dates straddle midnight.
        var start = new DateTime(2026, 9, 7, 18, 0, 0, DateTimeKind.Utc);
        var samples = Enumerable.Range(0, 9)
            .Select(i => new LearningSample(i + 1, 0, 13 + i, 250, 28, start.AddHours(i)))
            .ToList();

        var p = CongestionProfileLearner.Learn(samples).Profile;

        p.DaysSpanned.Should().Be(1);
        p.HasFullDayCycle.Should().BeFalse();
    }

    [Fact]
    public void HasFullDayCycle_OnceEveryHourHasASample()
    {
        var start = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        var samples = Enumerable.Range(0, 24)
            .Select(i => new LearningSample(i + 1, i < 12 ? 0 : 1, i, 250, 28, start.AddHours(i)))
            .ToList();

        CongestionProfileLearner.Learn(samples).Profile.HasFullDayCycle.Should().BeTrue();
    }
}
