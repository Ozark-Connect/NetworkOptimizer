using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The health gate in front of upstream discovery, and the degraded-run check after it. A run on a
/// down, saturated, or lossy WAN fails good hops at the reachability gate, so automatic runs wait
/// for a quiet WAN and a run that comes back far worse than the last committed one is discarded.
/// </summary>
public class UpstreamDiscoveryGateTests : IDisposable
{
    private const double Loaded = 0.50;

    // Acceptable idle loss, percent: a fiber-like profile and a lossy medium's.
    private const double FiberAcceptable = 0.05;
    private const double LossyAcceptable = 1.0;

    private readonly NetworkOptimizerDbContext _db;

    public UpstreamDiscoveryGateTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new NetworkOptimizerDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    // ---- Judge ----

    [Fact]
    public void Clean_inputs_pass()
    {
        var verdict = UpstreamDiscoveryGate.Judge(false, 0.10, 0.0, Loaded, FiberAcceptable);

        verdict.Ok.Should().BeTrue();
        verdict.Reason.Should().BeNull();
    }

    [Fact]
    public void Unknown_inputs_never_block()
    {
        UpstreamDiscoveryGate.Judge(false, null, null, Loaded, FiberAcceptable).Ok.Should().BeTrue();
    }

    [Fact]
    public void Open_outage_blocks()
    {
        var verdict = UpstreamDiscoveryGate.Judge(true, null, null, Loaded, FiberAcceptable);

        verdict.Ok.Should().BeFalse();
        verdict.Reason.Should().Be("an outage is in progress");
    }

    [Fact]
    public void Outage_is_named_ahead_of_load_and_loss()
    {
        UpstreamDiscoveryGate.Judge(true, 0.9, 5.0, Loaded, FiberAcceptable).Reason.Should().Be("an outage is in progress");
    }

    [Fact]
    public void Load_at_the_loaded_threshold_blocks()
    {
        var verdict = UpstreamDiscoveryGate.Judge(false, 0.73, 0.0, Loaded, FiberAcceptable);

        verdict.Ok.Should().BeFalse();
        verdict.Reason.Should().Be("running at 73% of plan");
    }

    [Fact]
    public void Load_below_the_loaded_threshold_passes()
    {
        // A busy business network sits just under the loaded line all day; it must still discover.
        UpstreamDiscoveryGate.Judge(false, 0.49, 0.0, Loaded, FiberAcceptable).Ok.Should().BeTrue();
    }

    [Fact]
    public void Loss_above_the_profiles_acceptable_loss_blocks()
    {
        var verdict = UpstreamDiscoveryGate.Judge(false, 0.05, 2.34, Loaded, FiberAcceptable);

        verdict.Ok.Should().BeFalse();
        verdict.Reason.Should().Be("2.3% packet loss in the last 10 minutes");
    }

    [Fact]
    public void Half_a_percent_loss_blocks_on_a_fiber_like_profile()
    {
        UpstreamDiscoveryGate.Judge(false, 0.05, 0.5, Loaded, FiberAcceptable).Ok.Should().BeFalse();
    }

    [Fact]
    public void Half_a_percent_loss_passes_on_a_lossy_medium()
    {
        // Starlink, cellular, and fixed wireless idle above a fiber line; a fixed floor would never let them discover.
        UpstreamDiscoveryGate.Judge(false, 0.05, 0.5, Loaded, LossyAcceptable).Ok.Should().BeTrue();
    }

    [Fact]
    public void Loss_at_the_acceptable_line_passes()
    {
        UpstreamDiscoveryGate.Judge(false, 0.05, 1.0, Loaded, LossyAcceptable).Ok.Should().BeTrue();
    }

    [Fact]
    public void Unknown_acceptable_loss_never_blocks_on_loss()
    {
        UpstreamDiscoveryGate.Judge(false, 0.05, 5.0, Loaded, acceptableLossPct: null).Ok.Should().BeTrue();
    }

    // ---- HasOpenOutage ----

    [Theory]
    [InlineData("monitoring.wan_outage", "wan", "wan", true)]
    [InlineData("monitoring.wan_outage_partial", "wan2", "wan2", true)]
    [InlineData("monitoring.wan_outage", "wan2", "wan", false)]
    [InlineData("monitoring.wan_outage", "all-wans", "wan", true)]
    [InlineData("monitoring.wan_outage", "all-wans", null, true)]
    [InlineData("monitoring.wan_outage", "wan", null, false)]
    [InlineData("monitoring.target_offline", "wan", "wan", false)]
    public void Outage_alerts_match_their_own_wan_or_the_site_rollup(string eventType, string deviceId, string? wanKey, bool expected)
    {
        var alerts = new[] { new AlertHistoryEntry { EventType = eventType, DeviceId = deviceId, Status = AlertStatus.Active } };

        UpstreamDiscoveryGate.HasOpenOutage(alerts, wanKey).Should().Be(expected);
    }

    [Fact]
    public void Resolved_outage_does_not_block()
    {
        var alerts = new[] { new AlertHistoryEntry { EventType = "monitoring.wan_outage", DeviceId = "wan", Status = AlertStatus.Resolved } };

        UpstreamDiscoveryGate.HasOpenOutage(alerts, "wan").Should().BeFalse();
    }

    // ---- Degraded run ----

    [Theory]
    [InlineData(6, 10, 0.10, true)]    // most failed after a clean baseline
    [InlineData(5, 10, 0.10, false)]   // exactly half is not "more than half"
    [InlineData(6, 10, 0.25, false)]   // the baseline already lost a quarter
    [InlineData(6, 10, null, false)]   // no baseline, no verdict
    [InlineData(0, 0, 0.0, false)]     // nothing checked
    public void Degraded_run_needs_a_clean_baseline_and_a_majority_failing(int failed, int checkedCount, double? baseline, bool expected)
    {
        UpstreamRediscoveryService.IsDegradedRun(failed, checkedCount, baseline).Should().Be(expected);
    }

    [Fact]
    public async Task Unreachable_share_round_trips_per_wan()
    {
        await UpstreamRediscoveryService.SaveUnreachableShareAsync(_db, "wan2", 0.125, CancellationToken.None);
        await _db.SaveChangesAsync();

        (await UpstreamRediscoveryService.LoadUnreachableShareAsync(_db, "wan2", CancellationToken.None)).Should().Be(0.125);
        (await UpstreamRediscoveryService.LoadUnreachableShareAsync(_db, "wan", CancellationToken.None)).Should().BeNull();

        await UpstreamRediscoveryService.SaveUnreachableShareAsync(_db, "wan2", 0.5, CancellationToken.None);
        await _db.SaveChangesAsync();
        (await UpstreamRediscoveryService.LoadUnreachableShareAsync(_db, "wan2", CancellationToken.None)).Should().Be(0.5);
    }
}
