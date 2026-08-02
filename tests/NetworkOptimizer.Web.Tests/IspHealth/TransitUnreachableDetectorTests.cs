using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

public class TransitUnreachableDetectorTests
{
    private static readonly IspHealthOptions Options = new();
    private static readonly DateTime Start = TestSeries.Start;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static List<TransitUnreachableDetector.DarkWindow> Detect(List<LatencySample> samples) =>
        TransitUnreachableDetector.Detect("t1", 3356, "Lumen", samples, Options);

    private static List<TransitUnreachableDetector.DarkWindow> DetectMostlyDark(List<LatencySample> samples) =>
        TransitUnreachableDetector.DetectMostlyDark("t1", 3356, "Lumen", samples, Options);

    /// <summary>
    /// A target that answers roughly one probe in three, for the given span. This is the shape that
    /// slipped through every filter: never a solid run so <see cref="TransitUnreachableDetector.Detect"/>
    /// never fires, healthy on a month-wide average so neither the flat-line nor the flaky check sees
    /// it, and ~100% loss for most of the samples that land inside a loaded window.
    /// </summary>
    private static List<LatencySample> Flapping(DateTime from, TimeSpan span, int answerEveryNth = 3)
    {
        var samples = new List<LatencySample>();
        var minutes = (int)span.TotalMinutes;
        for (var i = 0; i < minutes; i++)
            samples.Add(new LatencySample(from.AddMinutes(i), 12, 14, 1, i % answerEveryNth == 0 ? 0.5 : 100));
        return samples;
    }

    [Fact]
    public void Flapping_target_is_carved_out_even_though_it_never_goes_solidly_dark()
    {
        var samples = TestSeries.Flat(Start, Hour, 12, 0.5).Take(10).ToList();
        samples.AddRange(Flapping(Start.AddMinutes(10), TimeSpan.FromMinutes(40)));

        // The every-sample rule sees nothing: an answered probe resets it before it reaches 180 s.
        Detect(samples).Should().BeEmpty();

        var windows = DetectMostlyDark(samples);
        windows.Should().ContainSingle();
        windows[0].Start.Should().Be(Start.AddMinutes(11));
        windows[0].AsnNumber.Should().Be(3356);
    }

    [Fact]
    public void Brief_flapping_stays_in_the_loss_pool()
    {
        // Intermittent evidence is weaker than a solid washout, so it has to persist. Five minutes of
        // flapping is a loss burst and belongs in the pool.
        var samples = TestSeries.Flat(Start, Hour, 12, 0.5).Take(10).ToList();
        samples.AddRange(Flapping(Start.AddMinutes(10), TimeSpan.FromMinutes(5)));

        DetectMostlyDark(samples).Should().BeEmpty();
    }

    [Fact]
    public void A_target_answering_most_probes_is_never_carved_out()
    {
        // Answers 2 of every 3. Lossy, and that loss is real signal the score should keep.
        var samples = Flapping(Start, TimeSpan.FromMinutes(40), answerEveryNth: 2)
            .Select((s, i) => i % 3 == 0 ? s with { LossPercent = 100 } : s with { LossPercent = 0.5 })
            .ToList();

        DetectMostlyDark(samples).Should().BeEmpty();
    }

    [Fact]
    public void Recovery_ends_the_span_at_the_last_dark_sample()
    {
        // Masking must never hide samples the target actually answered after it came back.
        var samples = Flapping(Start, TimeSpan.FromMinutes(40)).ToList();
        samples.AddRange(TestSeries.Flat(Start.AddMinutes(40), TimeSpan.FromMinutes(20), 12, 0.5));

        var windows = DetectMostlyDark(samples);
        windows.Should().ContainSingle();
        windows[0].End.Should().BeBefore(Start.AddMinutes(40));
    }

    [Fact]
    public void Sustained_total_loss_becomes_a_dark_window()
    {
        // 20 minutes at 100% loss inside an otherwise clean hour (1-min samples).
        var samples = TestSeries.Flat(Start, Hour, 12, 0.5)
            .WithSegment(Start.AddMinutes(10), Start.AddMinutes(30), 12, 0.5, lossPct: 100);

        var windows = Detect(samples);

        windows.Should().ContainSingle();
        windows[0].Start.Should().Be(Start.AddMinutes(10));
        windows[0].End.Should().Be(Start.AddMinutes(29));
        windows[0].AsnNumber.Should().Be(3356);
    }

    [Fact]
    public void Short_total_loss_flap_stays_in_the_loss_pool()
    {
        // Two dark samples span only 60 s - a flap, below TransitUnreachableMinSeconds.
        var samples = TestSeries.Flat(Start, Hour, 12, 0.5)
            .WithSegment(Start.AddMinutes(10), Start.AddMinutes(12), 12, 0.5, lossPct: 100);

        Detect(samples).Should().BeEmpty();
    }

    [Fact]
    public void Lossy_but_reachable_transit_is_not_a_dark_window()
    {
        // A heavy but partial loss floor (40%) for a long stretch: still forwarding, so it
        // must keep feeding the access-layer loss pool.
        var samples = TestSeries.Flat(Start, Hour, 12, 0.5, lossPct: 40);

        Detect(samples).Should().BeEmpty();
    }

    [Fact]
    public void Monitoring_gap_inside_a_dark_run_does_not_split_it()
    {
        // Dark 10:00-10:10, a 4-min sample gap (console restart), dark again 10:14-10:24.
        var samples = TestSeries.Flat(Start, Hour, 12, 0.5)
            .WithSegment(Start.AddMinutes(10), Start.AddMinutes(25), 12, 0.5, lossPct: 100)
            .Where(s => s.Time < Start.AddMinutes(14) || s.Time >= Start.AddMinutes(18))
            .ToList();

        var windows = Detect(samples);

        windows.Should().ContainSingle();
        windows[0].Start.Should().Be(Start.AddMinutes(10));
        windows[0].End.Should().Be(Start.AddMinutes(24));
    }

    [Fact]
    public void Separate_episodes_stay_separate_windows()
    {
        // Two washouts an hour apart: each is its own window (and later its own path event).
        var samples = TestSeries.Flat(Start, TimeSpan.FromHours(3), 12, 0.5)
            .WithSegment(Start.AddMinutes(10), Start.AddMinutes(20), 12, 0.5, lossPct: 100)
            .WithSegment(Start.AddMinutes(90), Start.AddMinutes(105), 12, 0.5, lossPct: 100);

        var windows = Detect(samples);

        windows.Should().HaveCount(2);
        windows[0].Start.Should().Be(Start.AddMinutes(10));
        windows[1].Start.Should().Be(Start.AddMinutes(90));
    }

    [Fact]
    public void Merge_collapses_a_clusters_members_into_one_event()
    {
        var a = new TransitUnreachableDetector.DarkWindow("t1", 3356, "Lumen", Start.AddMinutes(10), Start.AddMinutes(30));
        var b = new TransitUnreachableDetector.DarkWindow("t2", 3356, "Lumen", Start.AddMinutes(12), Start.AddMinutes(33));

        var events = TransitUnreachableDetector.MergeByAsn(new[] { a, b }, Options);

        events.Should().ContainSingle();
        events[0].Start.Should().Be(Start.AddMinutes(10));
        events[0].End.Should().Be(Start.AddMinutes(33));
        events[0].TargetCount.Should().Be(2);
    }

    [Fact]
    public void Merge_keeps_distinct_episodes_and_distinct_asns_apart()
    {
        var early = new TransitUnreachableDetector.DarkWindow("t1", 3356, "Lumen", Start.AddMinutes(10), Start.AddMinutes(20));
        var late = new TransitUnreachableDetector.DarkWindow("t1", 3356, "Lumen", Start.AddMinutes(90), Start.AddMinutes(100));
        var other = new TransitUnreachableDetector.DarkWindow("t9", 1299, "Arelion", Start.AddMinutes(10), Start.AddMinutes(20));

        var events = TransitUnreachableDetector.MergeByAsn(new[] { early, late, other }, Options);

        events.Should().HaveCount(3);
        events.Count(e => e.AsnNumber == 3356).Should().Be(2);
        events.Count(e => e.AsnNumber == 1299).Should().Be(1);
    }
}
