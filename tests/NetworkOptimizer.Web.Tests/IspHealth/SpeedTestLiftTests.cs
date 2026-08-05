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
/// the figure there is nothing downstream able to correct it.
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
    {
        var rates = TestSeries.Throughput(TestSeries.Start, Day, 50, 5)
            .Select(r => r.Time >= LoadedStart && r.Time < LoadedEnd
                ? r with { DownloadBps = 800_000_000 }
                : r)
            .ToList();

        var hop = TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3)
            .WithSegment(LoadedStart, LoadedEnd, loadedHopRtt, 0.3);

        var inputs = new IspHealthInputs
        {
            WindowStart = TestSeries.Start,
            WindowEnd = TestSeries.Start + Day,
            FirstHopSeries = hop,
            AccessHopSeries = new List<List<LatencySample>> { hop },
            LossPoolSeries = new List<List<LatencySample>> { hop },
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
        // measured 25 ms of queue the probes, reading about 1 ms, never sampled.
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
    public void A_test_from_outside_the_episode_is_not_its_measurement()
    {
        var measured = LoadedDown(3.0);
        var far = LoadedDown(3.0, Test(TestSeries.Start.AddHours(2), 980, 31));

        far.Should().Be(measured);
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
