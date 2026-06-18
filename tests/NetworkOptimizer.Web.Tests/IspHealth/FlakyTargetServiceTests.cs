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
            for (var i = 0; i < losses.Length; i++)
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
