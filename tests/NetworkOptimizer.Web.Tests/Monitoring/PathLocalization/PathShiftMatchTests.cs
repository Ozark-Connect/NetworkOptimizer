using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathLocalization;

/// <summary>
/// A Path shift explains an episode whose degraded targets all sit on its new level; anything more (a
/// rise past the step, real loss, a target off the shift) stays congestion. Levels follow a real anycast
/// move: 1.6 ms to 17.7 ms on one resolver.
/// </summary>
public class PathShiftMatchTests
{
    private static readonly IspHealthOptions Options = new();
    private static readonly DateTime Step = new(2026, 11, 3, 11, 15, 0, DateTimeKind.Utc);
    private const string Target = "wan-cloudflare-1111";

    private static PathShiftEvent Shift(DateTime time, double before, double after, string target = Target) => new()
    {
        Time = time,
        TargetId = target,
        TargetIds = { target },
        BeforeMedianMs = before,
        AfterMedianMs = after,
        IsDestination = true
    };

    private static EpisodeTargetLevels Levels(double baseline = 1.63, double peak = 17.72, double loss = 0, string target = Target) =>
        new(target, baseline, peak, loss);

    private static PathShiftEvent? Explaining(DateTime opened, IReadOnlyList<PathShiftEvent> shifts, params EpisodeTargetLevels[] degraded) =>
        PathShiftMatch.Explaining(opened, degraded, shifts, Options);

    [Fact]
    public void EpisodeOnTheStep_IsExplained()
    {
        var shift = Shift(Step, 1.6, 17.7);
        // Opened a window ahead of the step's boundary, with one lost probe's worth of noise.
        Explaining(Step.AddMinutes(-5), new[] { shift }, Levels(loss: 1.33)).Should().BeSameAs(shift);
    }

    [Fact]
    public void LaterEpisodeAtTheSameLevel_IsExplained()
    {
        var shift = Shift(Step, 1.6, 17.7);
        Explaining(Step.AddHours(7), new[] { shift }, Levels(baseline: 1.65, peak: 17.44, loss: 0.69)).Should().BeSameAs(shift);
    }

    [Fact]
    public void RisePastTheStep_StaysCongestion()
    {
        Explaining(Step, new[] { Shift(Step, 1.6, 17.7) }, Levels(peak: 30)).Should().BeNull();
    }

    [Fact]
    public void RealLoss_StaysCongestion()
    {
        Explaining(Step, new[] { Shift(Step, 1.6, 17.7) }, Levels(loss: 4)).Should().BeNull();
    }

    [Fact]
    public void BaselineOffTheBeforeLevel_StaysCongestion()
    {
        // Degraded from 10 ms: not the rise this shift describes.
        Explaining(Step, new[] { Shift(Step, 1.6, 17.7) }, Levels(baseline: 10)).Should().BeNull();
    }

    [Fact]
    public void RecoveredBeforeTheEpisode_StaysCongestion()
    {
        // The path stepped back down before this episode, so its rise is a new one.
        var shifts = new[] { Shift(Step, 1.6, 17.7), Shift(Step.AddHours(2), 17.7, 1.6) };
        Explaining(Step.AddHours(5), shifts, Levels()).Should().BeNull();
    }

    [Fact]
    public void ShiftWellAfterTheEpisodeOpened_DoesNotExplainIt()
    {
        Explaining(Step.AddHours(-2), new[] { Shift(Step, 1.6, 17.7) }, Levels()).Should().BeNull();
    }

    [Fact]
    public void ShiftOnAnotherTarget_DoesNotExplainIt()
    {
        Explaining(Step, new[] { Shift(Step, 1.6, 17.7, "wan-google-8888") }, Levels()).Should().BeNull();
    }

    [Fact]
    public void UnreachableWindow_IsNotAStep()
    {
        var dark = new PathShiftEvent { Time = Step, TargetIds = { Target }, IsUnreachable = true, UnreachableEnd = Step.AddMinutes(10) };
        Explaining(Step, new[] { dark }, Levels()).Should().BeNull();
    }

    [Fact]
    public void EveryDegradedTarget_MustBeOnAShift()
    {
        var shift = Shift(Step, 1.6, 17.7);
        var other = Levels(baseline: 20, peak: 45, target: "aws-us-east-2");
        Explaining(Step, new[] { shift }, Levels(), other).Should().BeNull();
    }

    [Fact]
    public void CorrelatedShift_ReadsEachMembersOwnLevels()
    {
        var shift = new PathShiftEvent
        {
            Time = Step,
            TargetId = Target,
            TargetIds = { Target, "aws-us-west-2" },
            BeforeMedianMs = 1.6,
            AfterMedianMs = 17.7,
            CorrelatedTargetCount = 2,
            Members =
            {
                new PathShiftMember("Cloudflare", 1.6, 17.7, new[] { Target }),
                new PathShiftMember("AWS us-west-2", 20, 36, new[] { "aws-us-west-2" })
            }
        };
        var west = Levels(baseline: 20.2, peak: 36.4, target: "aws-us-west-2");
        Explaining(Step, new[] { shift }, Levels(), west).Should().BeSameAs(shift);
    }

    private static IspHealthReport Report(params PathShiftEvent[] shifts)
    {
        var report = new IspHealthReport
        {
            ComputedAt = Step.AddDays(1),
            WindowStart = Step.AddHours(-12),
            WindowEnd = Step.AddHours(12),
            Profile = IspHealthProfiles.GetProfile(AccessTechnology.Gpon)!,
            AccessTechnology = AccessTechnology.Gpon,
            AccessDimension = new IspScoreDimension { Name = "Access Layer", Score = 90, Weight = 0.5 },
            TransitDimension = new IspScoreDimension { Name = "Transit", Score = 85, Weight = 0.25 },
            IspAsnDimension = new IspScoreDimension { Name = "ISP Network", Score = 87, Weight = 0.25 }
        };
        report.PathShifts.AddRange(shifts);
        return report;
    }

    private static PathEpisodeView Episode(int id, DateTime opened, double baseline, double peak, double loss = 0) => new()
    {
        Id = id,
        OpenedAt = opened,
        ClosedAt = opened.AddHours(6),
        Disposition = CongestionDisposition.Unverifiable,
        Label = "Every router on the way answered cleanly.",
        Degraded = new[]
        {
            new PathEpisodeTargetView(1, Target, "Cloudflare (1.1.1.1)", PathEpisodeTargetRole.Degraded, baseline, peak, 0, loss, null)
        }
    };

    [Fact]
    public void Fold_GivesTheShiftItsEarliestTrace_AndHidesEveryExplainedRow()
    {
        var shift = Shift(Step, 1.6, 17.7);
        var report = Report(shift);
        var episodes = new[]
        {
            Episode(4, Step.AddHours(7), 1.65, 17.44, 0.69),
            Episode(3, Step.AddMinutes(-5), 1.63, 17.72, 1.33),
            Episode(5, Step.AddHours(9), 1.65, 40)
        };

        var fold = PathEpisodePresentation.FoldIntoShifts(episodes, report, Options);

        fold.Explained.Should().BeEquivalentTo(new[] { 3, 4 });
        fold.TraceByShift[shift].Should().Be(3);
        fold.Unexplained(episodes, e => e.Id).Select(e => e.Id).Should().Equal(5);

        var row = IspHealthPresentation.EventTimeline(report, fold.TraceByShift).Single(e => e.Category == EventCategory.Shift);
        row.Badge.Should().Be("Path shift");
        row.EpisodeId.Should().Be(3);
    }

    // ---- Step-only episodes: no detected shift, RTT rose on its own with loss and jitter at baseline ----

    private static EpisodeTargetLevels StepLevels(string target, string name, double baseline, double peak,
        double baseJitter = 0.4, double peakJitter = 0.6, double loss = 0) =>
        new(target, baseline, peak, loss, baseJitter, peakJitter, name);

    private static readonly EpisodeTargetLevels West = StepLevels("aws-us-west-1", "AWS us-west-1", 58.2, 71.0);
    private static readonly EpisodeTargetLevels Anycast = StepLevels("path-cdn-as64500", "CDN", 18.0, 34.2);

    [Fact]
    public void StepOnlyEpisode_WithNoDetectedShift_IsAPathShift()
    {
        var shift = Explaining(Step, Array.Empty<PathShiftEvent>(), West, Anycast);

        shift.Should().NotBeNull();
        shift!.Time.Should().Be(Step);
        shift.Direction.Should().Be(PathShiftDirection.Up);
        shift.AsnName.Should().Be("CDN", "the lowest before-level names it, as a correlated shift is named");
        shift.BeforeMedianMs.Should().Be(18.0);
        shift.AfterMedianMs.Should().Be(34.2);
        shift.CorrelatedTargetCount.Should().Be(2);
        shift.Members.Select(m => (m.Name, m.BeforeMedianMs, m.AfterMedianMs))
            .Should().Equal(("CDN", 18.0, 34.2), ("AWS us-west-1", 58.2, 71.0));
    }

    [Fact]
    public void WideningJitter_StaysCongestion()
    {
        // Twice the baseline and over 0.8 ms above it: a queue, not a longer route.
        var queued = StepLevels("aws-us-west-1", "AWS us-west-1", 58.2, 71.0, baseJitter: 0.4, peakJitter: 1.9);
        PathShiftMatch.IsStep(queued, Options).Should().BeFalse();
        Explaining(Step, Array.Empty<PathShiftEvent>(), queued, Anycast).Should().BeNull("every degraded target must be a step");
    }

    [Fact]
    public void SmallJitterRiseOnALowBaseline_IsStillAStep()
    {
        // 0.15 to 0.81 ms on a real 9.7 to 23.8 ms route change: under the 0.8 ms floor over baseline.
        PathShiftMatch.IsStep(StepLevels("t", "T", 9.7, 23.8, baseJitter: 0.15, peakJitter: 0.81), Options).Should().BeTrue();
    }

    [Fact]
    public void LossOnAStep_StaysCongestion()
    {
        PathShiftMatch.IsStep(StepLevels("t", "T", 18, 34, loss: 3), Options).Should().BeFalse();
    }

    [Fact]
    public void WithoutJitterReadings_NotJudgedAStep()
    {
        // Episodes stored before jitter was kept, or a probe that reports none, stay congestion.
        PathShiftMatch.IsStep(Levels(), Options).Should().BeFalse();
        PathShiftMatch.IsStep(new EpisodeTargetLevels("t", 18, 34, 0, 0.4, null), Options).Should().BeFalse();
    }

    [Fact]
    public void DetectedShift_WinsOverTheStepShift()
    {
        var detected = Shift(Step, 1.6, 17.7);
        Explaining(Step, new[] { detected }, StepLevels(Target, "Cloudflare", 1.63, 17.72)).Should().BeSameAs(detected);
    }

    [Fact]
    public void RisePastADetectedStep_WithFlatJitter_IsStillAPathShift()
    {
        // The detected shift does not explain a peak of 30, but nothing queued: the route changed further.
        var shift = Explaining(Step, new[] { Shift(Step, 1.6, 17.7) }, StepLevels(Target, "Cloudflare", 1.63, 30));
        shift.Should().NotBeNull();
        shift!.AfterMedianMs.Should().Be(30);
    }

    [Fact]
    public void Fold_DrawsAStepOnlyEpisodeAsAPathShiftRow_WithItsTrace()
    {
        var report = Report();
        var episodes = new[]
        {
            new PathEpisodeView
            {
                Id = 7,
                OpenedAt = Step,
                ClosedAt = Step.AddHours(6),
                Disposition = CongestionDisposition.Unverifiable,
                Label = "Both slowed down together.",
                Degraded = new[]
                {
                    new PathEpisodeTargetView(1, "aws-us-west-1", "AWS us-west-1", PathEpisodeTargetRole.Degraded, 58.2, 71.0, 0.5, 0, null,
                        BaselineJitterMs: 0.4, PeakJitterMs: 0.6),
                    new PathEpisodeTargetView(2, "path-cdn-as64500", "CDN", PathEpisodeTargetRole.Degraded, 18.0, 34.2, 0.5, 0, null,
                        BaselineJitterMs: 0.3, PeakJitterMs: 0.5)
                }
            }
        };

        var fold = PathEpisodePresentation.FoldIntoShifts(episodes, report, Options);

        fold.Explained.Should().Equal(7);
        fold.Unexplained(episodes, e => e.Id).Should().BeEmpty("no congestion row, finding, or banner");
        var rows = IspHealthPresentation.EventTimeline(report, fold.TraceByShift).ToList();
        var row = rows.Single();
        row.Badge.Should().Be("Path shift");
        row.EpisodeId.Should().Be(7);
        row.Text.Should().StartWith("RTT stepped up 16.2 ms on CDN (18 to 34.2 ms), seen on 2 paths.");
        row.Members.Should().Equal("CDN - 18 to 34.2 ms", "AWS us-west-1 - 58.2 to 71 ms");
    }

    private static PathEpisodeView StepEpisodeView(int asn, bool isDestination) => new()
    {
        Id = 8,
        OpenedAt = Step,
        ClosedAt = Step.AddHours(2),
        Disposition = CongestionDisposition.Unverifiable,
        Label = "Slowed down.",
        Degraded = new[]
        {
            new PathEpisodeTargetView(1, "t1", "Hop", PathEpisodeTargetRole.Degraded, 18.0, 34.2, 0.5, 0, null,
                BaselineJitterMs: 0.3, PeakJitterMs: 0.5, AsnNumber: asn, IsDestination: isDestination)
        }
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StepShift_CarriesTheLeadTargetsAsnAndType(bool isDestination)
    {
        var episode = StepEpisodeView(64500, isDestination);
        var shift = PathEpisodePresentation.FoldIntoShifts(new[] { episode }, Report(), Options).TraceByShift.Keys.Single();

        shift.AsnNumber.Should().Be(64500, "the row labels the network as a detected shift does");
        shift.IsDestination.Should().Be(isDestination, "only an InternetService target is a destination");
    }

    [Fact]
    public void ChartBand_ExplainedEpisodesArePathShifts_TheRestStayCongestion()
    {
        var report = Report(Shift(Step, 1.6, 17.7));
        var detected = Episode(3, Step.AddMinutes(-5), 1.63, 17.72);
        var stepOnly = StepEpisodeView(64500, isDestination: true);
        var congestion = Episode(5, Step.AddHours(9), 1.65, 40);
        var episodes = new[] { detected, stepOnly, congestion };

        var fold = PathEpisodePresentation.FoldIntoShifts(episodes, report, Options);

        PathEpisodePresentation.ChartBand(detected, fold).Should().Be(("path-shift", "Path shift"));
        PathEpisodePresentation.ChartBand(stepOnly, fold).Should().Be(("path-shift", "Path shift"));
        PathEpisodePresentation.ChartBand(congestion, fold).Should().Be(("congestion", PathEpisodePresentation.BadgeLabel(congestion)));
    }

    [Fact]
    public void ChartBand_WithoutAReport_IsCongestion()
    {
        var e = Episode(3, Step, 1.63, 17.72);
        PathEpisodePresentation.ChartBand(e, PathEpisodePresentation.ShiftFold.None).Type.Should().Be("congestion");
    }

    [Fact]
    public void Fold_WithoutAReport_ExplainsNothing()
    {
        var episodes = new[] { Episode(3, Step, 1.63, 17.72) };
        PathEpisodePresentation.FoldIntoShifts(episodes, null, Options).Explained.Should().BeEmpty();
        IspHealthPresentation.EventTimeline(Report(Shift(Step, 1.6, 17.7)))
            .Single().EpisodeId.Should().BeNull();
    }
}
