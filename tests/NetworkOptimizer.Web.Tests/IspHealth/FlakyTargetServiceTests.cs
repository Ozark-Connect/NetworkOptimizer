using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Flaky-target detector (#849) analysis logic. Targets the median-bin-loss criterion: a target is
/// flagged when its median surviving-bin loss is at/above the threshold (>= 4x peer median AND >= 3%
/// absolute), which catches a bursty-but-consistently-lossy hop while ignoring a one-off spike.
/// </summary>
public class FlakyTargetServiceTests
{
    private static readonly DateTime Base = new(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);

    private static (Dictionary<string, Dictionary<DateTime, double>> Loss, Dictionary<string, MonitoringTarget> Meta) Build(
        params (string Id, double[] Losses)[] targets)
    {
        var loss = new Dictionary<string, Dictionary<DateTime, double>>();
        var meta = new Dictionary<string, MonitoringTarget>();
        foreach (var (id, losses) in targets)
        {
            var bins = new Dictionary<DateTime, double>();
            // A negative loss is a gap sentinel: no bin reported at that slot.
            for (var i = 0; i < losses.Length; i++)
                if (losses[i] >= 0)
                    bins[Base.AddMinutes(10 * i)] = losses[i];
            loss[id] = bins;
            meta[id] = new MonitoringTarget
            {
                TargetId = id,
                Id = meta.Count + 1,
                Name = id,
                Address = id,
                TargetType = MonitoringTargetType.Transit
            };
        }
        return (loss, meta);
    }

    [Fact]
    public void Bursty_but_consistently_lossy_target_is_flagged()
    {
        // The real Mac case: a Level 3 edge hop against zero-loss peers. Trimmed mean drops the
        // 0% and 17.5% bins and averages the rest -> ~4.1%.
        var (loss, meta) = Build(
            ("level3-edge", new[] { 0.0, 1.05, 6.21, 6.4, 2.86, 17.5 }),
            ("clean-a", new[] { 0.0, 0, 0, 0, 0, 0 }),
            ("clean-b", new[] { 0.0, 0, 0, 0, 0, 0 }));

        var flaky = FlakyTargetService.Analyze(loss, meta);

        flaky.Should().ContainSingle().Which.TargetId.Should().Be("level3-edge");
        flaky[0].LossPct.Should().BeApproximately(4.13, 0.3);
    }

    [Fact]
    public void Trimmed_mean_holds_where_a_small_sample_median_would_flap()
    {
        // The 5-bin snapshot from the observed flap: median is 2.86% (< 3%, would DROP the target)
        // but the trimmed mean is 3.37% (>= 3%, stays flagged). This is the anti-flap fix.
        var (loss, meta) = Build(
            ("level3-edge", new[] { 0.0, 1.05, 2.86, 6.21, 6.4 }),
            ("clean-a", new[] { 0.0, 0, 0, 0, 0 }),
            ("clean-b", new[] { 0.0, 0, 0, 0, 0 }));

        FlakyTargetService.Analyze(loss, meta).Should().ContainSingle()
            .Which.TargetId.Should().Be("level3-edge");
    }

    [Fact]
    public void Single_spike_on_an_otherwise_clean_target_is_not_flagged()
    {
        // Median stays ~0, so one bad bin can't masquerade as a flaky target.
        var (loss, meta) = Build(
            ("one-spike", new[] { 0.0, 0, 0, 0, 0, 30.0 }),
            ("clean-a", new[] { 0.0, 0, 0, 0, 0, 0 }),
            ("clean-b", new[] { 0.0, 0, 0, 0, 0, 0 }));

        FlakyTargetService.Analyze(loss, meta).Should().BeEmpty();
    }

    [Fact]
    public void Target_with_too_few_bins_is_not_judged()
    {
        // Only 2 surviving bins (< MinTargetBins of 3) - not enough data yet.
        var (loss, meta) = Build(
            ("new-lossy", new[] { 20.0, 20.0 }),
            ("clean-a", new[] { 0.0, 0, 0, 0, 0, 0 }),
            ("clean-b", new[] { 0.0, 0, 0, 0, 0, 0 }));

        FlakyTargetService.Analyze(loss, meta).Should().BeEmpty();
    }

    // ---- Recovery gate: stable -> flaky -> stable stops being presented ----

    /// <summary>Losses: `head` copies of `headLoss` followed by `tail` copies of `tailLoss`.</summary>
    private static double[] Run(int head, double headLoss, int tail, double tailLoss) =>
        Enumerable.Repeat(headLoss, head).Concat(Enumerable.Repeat(tailLoss, tail)).ToArray();

    [Fact]
    public void Recovered_target_is_no_longer_flagged()
    {
        // Flaky for the first 2h, clean for the following 4h. The full-window trimmed mean
        // (~6.5%) still says flaky, but the trailing 3h is clean - recovered, not presented.
        var (loss, meta) = Build(
            ("recovered", Run(12, 20.0, 24, 0.0)),
            ("clean-a", Run(36, 0.0, 0, 0.0)),
            ("clean-b", Run(36, 0.0, 0, 0.0)));

        FlakyTargetService.Analyze(loss, meta).Should().BeEmpty();
    }

    [Fact]
    public void Always_flaky_target_with_long_history_stays_flagged()
    {
        // Flaky since it was added, including the trailing window - the recovery gate must not
        // clear it.
        var (loss, meta) = Build(
            ("always-flaky", Run(36, 8.0, 0, 0.0)),
            ("clean-a", Run(36, 0.0, 0, 0.0)),
            ("clean-b", Run(36, 0.0, 0, 0.0)));

        FlakyTargetService.Analyze(loss, meta).Should().ContainSingle()
            .Which.TargetId.Should().Be("always-flaky");
    }

    [Fact]
    public void Target_that_went_dark_cannot_prove_recovery_and_stays_flagged()
    {
        // Flaky, then stopped reporting entirely while peers kept going: no recent bins means
        // no evidence of stability, so it stays flagged.
        var (loss, meta) = Build(
            ("went-dark", Run(12, 20.0, 24, -1.0)),
            ("clean-a", Run(36, 0.0, 0, 0.0)),
            ("clean-b", Run(36, 0.0, 0, 0.0)));

        FlakyTargetService.Analyze(loss, meta).Should().ContainSingle()
            .Which.TargetId.Should().Be("went-dark");
    }

    [Fact]
    public void Too_few_recent_bins_do_not_clear_the_flag()
    {
        // Only 3 clean bins inside the recovery window (< MinRecoveryBins of 6): not enough
        // evidence to call it stable again.
        var (loss, meta) = Build(
            ("barely-back", Run(12, 20.0, 21, -1.0).Concat(Run(3, 0.0, 0, 0.0)).ToArray()),
            ("clean-a", Run(36, 0.0, 0, 0.0)),
            ("clean-b", Run(36, 0.0, 0, 0.0)));

        FlakyTargetService.Analyze(loss, meta).Should().ContainSingle()
            .Which.TargetId.Should().Be("barely-back");
    }

    [Fact]
    public void Hard_down_target_recovers_after_a_short_clean_streak()
    {
        // A total outage (100% bins) that ended: three clean bins (~30 min) clear it via the
        // hard-down fast path, long before the outage bins dilute out of the recovery window.
        var (loss, meta) = Build(
            ("outage-over", Run(12, 100.0, 3, 0.0)),
            ("clean-a", Run(15, 0.0, 0, 0.0)),
            ("clean-b", Run(15, 0.0, 0, 0.0)));

        FlakyTargetService.Analyze(loss, meta).Should().BeEmpty();
    }

    [Fact]
    public void Hard_down_target_still_down_stays_flagged()
    {
        var (loss, meta) = Build(
            ("still-down", Run(15, 100.0, 0, 0.0)),
            ("clean-a", Run(15, 0.0, 0, 0.0)),
            ("clean-b", Run(15, 0.0, 0, 0.0)));

        FlakyTargetService.Analyze(loss, meta).Should().ContainSingle()
            .Which.TargetId.Should().Be("still-down");
    }

    [Fact]
    public void Partial_loss_target_does_not_get_the_hard_down_fast_path()
    {
        // Same shape as the outage case but with partial (deprioritization-style) loss: a short
        // clean streak is NOT enough - it must satisfy the full recovery window instead.
        var (loss, meta) = Build(
            ("partial-loss", Run(12, 20.0, 3, 0.0)),
            ("clean-a", Run(15, 0.0, 0, 0.0)),
            ("clean-b", Run(15, 0.0, 0, 0.0)));

        FlakyTargetService.Analyze(loss, meta).Should().ContainSingle()
            .Which.TargetId.Should().Be("partial-loss");
    }

    [Fact]
    public void Outage_with_a_partial_boundary_bin_still_gets_the_fast_path()
    {
        // Real recovery shape: the aggregate bin straddling the moment the hop came back
        // averages to partial loss (observed 52.8%). The median over-threshold bin is still
        // 100%, so the boundary bin must not disqualify the fast path.
        var (loss, meta) = Build(
            ("boundary", Run(11, 100.0, 1, 52.8).Concat(Run(3, 0.0, 0, 0.0)).ToArray()),
            ("clean-a", Run(15, 0.0, 0, 0.0)),
            ("clean-b", Run(15, 0.0, 0, 0.0)));

        FlakyTargetService.Analyze(loss, meta).Should().BeEmpty();
    }

    [Fact]
    public void Genuinely_mixed_partial_loss_episode_does_not_get_the_fast_path()
    {
        // Half the over-threshold bins are partial (deprioritization-style) loss: the median
        // falls below the hard-down bar, so the short streak must not clear it.
        var (loss, meta) = Build(
            ("mixed", Run(6, 100.0, 6, 20.0).Concat(Run(3, 0.0, 0, 0.0)).ToArray()),
            ("clean-a", Run(15, 0.0, 0, 0.0)),
            ("clean-b", Run(15, 0.0, 0, 0.0)));

        FlakyTargetService.Analyze(loss, meta).Should().ContainSingle()
            .Which.TargetId.Should().Be("mixed");
    }

    [Fact]
    public void Target_flaky_again_after_a_clean_stretch_is_flagged()
    {
        // Clean history, then flaky through the trailing window - the recovery gate only clears
        // targets that are currently stable, never ones that are currently flaky.
        var (loss, meta) = Build(
            ("relapsed", Run(18, 0.0, 18, 8.0)),
            ("clean-a", Run(36, 0.0, 0, 0.0)),
            ("clean-b", Run(36, 0.0, 0, 0.0)));

        FlakyTargetService.Analyze(loss, meta).Should().ContainSingle()
            .Which.TargetId.Should().Be("relapsed");
    }

    [Fact]
    public void Shared_loss_bins_are_excluded_so_an_outage_does_not_flag_everyone()
    {
        // All three lose heavily in the first three bins (a path-wide outage), clean after.
        // Those bins are excluded, leaving every target with a clean median.
        var (loss, meta) = Build(
            ("a", new[] { 30.0, 30, 30, 0, 0, 0 }),
            ("b", new[] { 30.0, 30, 30, 0, 0, 0 }),
            ("c", new[] { 30.0, 30, 30, 0, 0, 0 }));

        FlakyTargetService.Analyze(loss, meta).Should().BeEmpty();
    }
}
