using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// A WAN speed test measures the same event on purpose and at full saturation, while the latency
/// probes only sample it on their own cadence - so a short event's peak queue can build and drain
/// between two probes unseen. The test stands in only where it read HIGHER, which is the one
/// direction passive sampling fails in.
/// <para>
/// That asymmetry is only fair while neither instrument can over-read, so a test that never filled
/// the pipe is refused: it did not load the buffers, and since the substitution only ever raises
/// the figure there is nothing downstream able to correct it. A test the probe cohort sampled and
/// read clean is refused too: the probes did not miss its peak, so the test is the outlier.
/// </para>
/// <para>
/// Distinct from the older wholesale fallback, which takes the speed tests' own deltas when the
/// series yields no loaded figure AT ALL - a path that is no longer reachable while there are
/// loaded windows, since a line whose every episode read clean now answers 0 rather than nothing.
/// </para>
/// </summary>
public class SpeedTestLiftTests
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly DateTime LoadedStart = TestSeries.Start.AddHours(12);
    private static readonly DateTime LoadedEnd = TestSeries.Start.AddHours(18);
    private static readonly AccessProfile Gpon = IspHealthProfiles.GetProfile(AccessTechnology.Gpon)!;
    private static readonly IspHealthOptions Options = new();

    /// <param name="loadedHopRtt">
    /// What the probes themselves saw under load. The idle floor is 2.0, so 3.0 is a measured
    /// delta of about 1 ms; passing 2.0 leaves the series flat, which now reads as a clean line
    /// rather than as an absent measurement.
    /// </param>
    private static double? LoadedDown(double loadedHopRtt, params SpeedTestSample[] tests)
        => LoadedDown(loadedHopRtt, targets: 1, tests);

    /// <param name="targets">
    /// How many probe targets sample the line. One is too few to corroborate anything, so its
    /// clean reading never overrules a test; a cohort of four or more is.
    /// </param>
    private static double? LoadedDown(double loadedHopRtt, int targets, params SpeedTestSample[] tests)
    {
        var rates = TestSeries.Throughput(TestSeries.Start, Day, 50, 5)
            .Select(r => r.Time >= LoadedStart && r.Time < LoadedEnd
                ? r with { DownloadBps = 800_000_000 }
                : r)
            .ToList();

        var hops = Enumerable.Range(0, targets)
            .Select(_ => TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3)
                .WithSegment(LoadedStart, LoadedEnd, loadedHopRtt, 0.3))
            .ToList();

        var inputs = new IspHealthInputs
        {
            WindowStart = TestSeries.Start,
            WindowEnd = TestSeries.Start + Day,
            FirstHopSeries = hops[0],
            AccessHopSeries = hops,
            LossPoolSeries = hops,
            WanRates = rates,
            ExpectedDownloadMbps = 1000,
            ExpectedUploadMbps = 500,
            ExpectedSpeedSource = "UniFi Network",
            WanSpeedTests = tests.ToList()
        };

        var text = new IspHealthScorer(Options).Score(inputs, Gpon)
            .AccessDimension.Factors.Single(f => f.Name == "Loaded Latency").ValueText;

        return double.TryParse(text?.Split(" ms down")[0], out var v) ? v : null;
    }

    private static SpeedTestSample Test(DateTime at, double downMbps, double loadedMs, double? idleMs = 6) =>
        new(at, downMbps, 490, PingMs: idleMs, DownloadLatencyMs: loadedMs, UploadLatencyMs: 8);

    [Fact]
    public void A_saturating_test_that_saw_more_queue_than_the_probes_did_sets_the_figure()
    {
        // 980 of a 1000 plan, 31 ms under load against its own 6 ms idle: it filled the pipe and
        // measured 25 ms of queue the probes, reading about 1 ms, never sampled. One target is too
        // few to contradict it - see the cohort cases below.
        var measured = LoadedDown(3.0);
        var lifted = LoadedDown(3.0, Test(LoadedStart.AddHours(1), 980, 31));

        // Higher, not 25: the lift is confined to the ONE episode the test overlapped, and the
        // factor is the median across every episode in the window. A single test moving the whole
        // figure to its own reading would be exactly the unconfined bias this avoids.
        lifted.Should().BeGreaterThan(measured!.Value);
    }

    [Fact]
    public void A_test_that_never_filled_the_pipe_is_refused()
    {
        // Same 25 ms at a fifth of plan - it never loaded the buffers, so whatever it measured was
        // not this link at saturation. This is the case that would otherwise bias every matched
        // episode upward with nothing able to pull it back.
        var measured = LoadedDown(3.0);
        var lifted = LoadedDown(3.0, Test(LoadedStart.AddHours(1), 200, 31));

        lifted.Should().Be(measured);
    }

    [Fact]
    public void A_test_reading_lower_than_the_probes_does_not_pull_the_figure_down()
    {
        var measured = LoadedDown(3.0);
        var clean = LoadedDown(3.0, Test(LoadedStart.AddHours(1), 980, 6.1));

        clean.Should().Be(measured);
    }

    [Fact]
    public void A_saturating_test_outside_any_load_episode_still_counts()
    {
        // Whether the rate series marked a test's few seconds as loaded says nothing about the
        // test. It filled the pipe on its own terms, so it is measured evidence either way.
        var measured = LoadedDown(3.0);
        var outside = LoadedDown(3.0, Test(TestSeries.Start.AddHours(2), 980, 31));

        outside.Should().BeApproximately(25, 1);
        outside.Should().BeGreaterThan(measured!.Value);
    }

    [Fact]
    public void A_test_the_probe_cohort_sampled_and_read_clean_is_dropped()
    {
        // Five targets sampled the test's loaded seconds and all read about 1 ms. The probes did
        // not miss a peak here, so the test's 25 ms is the outlier and cannot outvote them.
        var measured = LoadedDown(3.0, targets: 5);
        var contradicted = LoadedDown(3.0, targets: 5, Test(LoadedStart.AddHours(1), 980, 31));

        contradicted.Should().Be(measured);
    }

    [Fact]
    public void A_test_the_probe_cohort_corroborated_still_lifts_the_figure()
    {
        // The cohort saw about 8 ms of queue, so it agrees the line was loaded and elevated. The
        // test measured more of the same event at full saturation, and its figure stands.
        var measured = LoadedDown(10.0, targets: 5);
        var lifted = LoadedDown(10.0, targets: 5, Test(LoadedStart.AddHours(1), 980, 31));

        lifted.Should().BeGreaterThan(measured!.Value);
    }

    [Fact]
    public void A_contradicted_outlier_gives_way_to_a_clean_test_outside_the_episode()
    {
        // The shape of a real report: one scheduled test inside a load episode read 18.6 ms while
        // the cohort beside it read clean, and the next test ran outside any episode and read
        // 1.5 ms. Only the clean test speaks, so the figure stays near what everything else saw.
        var withBoth = LoadedDown(3.0, targets: 5,
            Test(LoadedStart.AddHours(1), 980, 24.6),
            Test(LoadedEnd.AddHours(2), 980, 7.5));

        withBoth.Should().BeLessThan(3);
    }

    [Fact]
    public void A_test_without_its_own_idle_reference_is_unusable()
    {
        // The delta is loaded-minus-idle from the SAME probe seconds apart. With no idle figure
        // there is nothing to subtract, and borrowing our baseline would reintroduce every blind
        // spot the substitution exists to avoid.
        var measured = LoadedDown(3.0);
        var noIdle = LoadedDown(3.0, Test(LoadedStart.AddHours(1), 980, 31, idleMs: null));

        noIdle.Should().Be(measured);
    }

    [Fact]
    public void Recent_clean_tests_outrank_an_older_bad_one()
    {
        // The regression this exists for. Taking the highest qualifying test in the window meant
        // one bad day outranked every clean test since, so a line whose recent tests are all clean
        // kept reporting its worst reading from a week ago - and it walked straight past the
        // clean-run verdict that had already decided the line was fixed.
        var oldBad = Test(LoadedStart.AddMinutes(10), 980, 31);
        var recentClean = new[]
        {
            Test(LoadedStart.AddHours(3), 980, 6.2),
            Test(LoadedStart.AddHours(4), 980, 6.1),
            Test(LoadedStart.AddHours(5), 980, 6.3),
        };

        var withHistory = LoadedDown(3.0, new[] { oldBad }.Concat(recentClean).ToArray());

        withHistory.Should().BeLessThan(10);
    }

    [Fact]
    public void A_site_whose_probes_saw_nothing_still_gets_what_its_test_measured()
    {
        // Since load episodes that all read clean became a real answer rather than no-answer, a
        // flat series returns 0 instead of null and never reaches the older wholesale fallback.
        // The lift covers that hole from the other side: the site is not left blind just because
        // its probes never sampled the queue its own test measured.
        var flat = LoadedDown(2.0, Test(LoadedStart.AddHours(1), 980, 31));

        flat.Should().BeApproximately(25, 1);
    }
}
