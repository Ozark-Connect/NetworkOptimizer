using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Unit tests for the resolver's PON O5-state derivation from the link-status series. The key
/// property: a missing/omitted status (Unknown) is absence of data, never a link-down, and a
/// source that never reports an O-state yields null so the factor can't false-alarm.
/// </summary>
public class PhysicalLinkResolverTests
{
    private static MonitoringInfluxClient.OntPoint Pt(string? status) =>
        new() { Time = System.DateTime.UnixEpoch, PonLinkStatus = status };

    [Fact]
    public void Operational_is_null_when_no_o_state_is_ever_reported()
    {
        // DDM sticks and most ONTs never report an O-state - the field is simply absent.
        var pts = new[] { Pt(null), Pt(null), Pt(null) };
        PhysicalLinkResolver.ResolveOperationalFromHistory(pts).Should().BeNull();
    }

    [Fact]
    public void Operational_is_true_when_every_reported_state_is_operation()
    {
        var pts = new[] { Pt("operation"), Pt("operation"), Pt("operation") };
        PhysicalLinkResolver.ResolveOperationalFromHistory(pts).Should().BeTrue();
    }

    [Fact]
    public void A_single_missing_status_among_operation_samples_is_not_a_break()
    {
        // The bug this fixes: a poll that drops the PON Link Status row (gateway stats page hiccup)
        // lands as null/Unknown and must NOT read as a link-down.
        var pts = new[] { Pt("operation"), Pt(null), Pt("operation") };
        PhysicalLinkResolver.ResolveOperationalFromHistory(pts).Should().BeTrue();
    }

    [Fact]
    public void A_known_non_operation_state_in_the_window_is_a_break()
    {
        var pts = new[] { Pt("operation"), Pt("popup"), Pt("operation") };
        PhysicalLinkResolver.ResolveOperationalFromHistory(pts).Should().BeFalse();
    }

    [Fact]
    public void Influx_status_strings_round_trip_through_the_parser()
    {
        // ToInfluxValue() lower-cases the state; the parser must still recognize it.
        PhysicalLinkResolver.ResolveOperationalFromHistory(new[] { Pt("ranging") }).Should().BeFalse();
        PhysicalLinkResolver.ResolveOperationalFromHistory(new[] { Pt("emergency_stop") }).Should().BeFalse();
        PhysicalLinkResolver.ResolveOperationalFromHistory(new[] { Pt("unknown") }).Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // Cellular network mode
    // ---------------------------------------------------------------------------

    private static readonly DateTime ModeStart = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);

    private static MonitoringInfluxClient.CellularPoint Cell(DateTime time, string mode) =>
        new() { Time = time, NetworkMode = mode };

    /// <summary>
    /// NSA emits an LTE point alongside every 5G point, so an ordered merge of both series ends
    /// on LTE for a link that never left 5G.
    /// </summary>
    private static List<MonitoringInfluxClient.CellularPoint> NsaSeries(int buckets)
    {
        var pts = new List<MonitoringInfluxClient.CellularPoint>();
        for (var i = 0; i < buckets; i++)
        {
            var t = ModeStart.AddMinutes(5 * i);
            pts.Add(Cell(t, "5G NSA"));
            pts.Add(Cell(t, "LTE"));
        }
        return pts;
    }

    [Fact]
    public void Healthy_nsa_link_is_not_reported_as_downgraded()
    {
        var (had5g, mode, downgraded) =
            PhysicalLinkResolver.ResolveCellularMode(NsaSeries(12), Bucket, live: null);

        had5g.Should().BeTrue();
        mode.Should().Be("5G NSA");
        downgraded.Should().BeFalse();
    }

    [Fact]
    public void Link_that_lost_5g_partway_through_is_reported_as_downgraded()
    {
        var pts = NsaSeries(6);
        for (var i = 6; i < 24; i++)
            pts.Add(Cell(ModeStart.AddMinutes(5 * i), "LTE"));

        var (had5g, mode, downgraded) =
            PhysicalLinkResolver.ResolveCellularMode(pts, Bucket, live: null);

        had5g.Should().BeTrue();
        mode.Should().Be("LTE");
        downgraded.Should().BeTrue();
    }

    [Fact]
    public void One_missing_5g_bucket_is_a_dropped_sample_not_a_downgrade()
    {
        var pts = NsaSeries(12);
        pts.Add(Cell(ModeStart.AddMinutes(60), "LTE"));

        PhysicalLinkResolver.ResolveCellularMode(pts, Bucket, live: null)
            .downgraded.Should().BeFalse();
    }

    [Fact]
    public void Lte_only_modem_never_reports_a_downgrade()
    {
        var pts = Enumerable.Range(0, 12)
            .Select(i => Cell(ModeStart.AddMinutes(5 * i), "LTE"))
            .ToList();

        var (had5g, mode, downgraded) =
            PhysicalLinkResolver.ResolveCellularMode(pts, Bucket, live: null);

        had5g.Should().BeFalse();
        mode.Should().Be("LTE");
        downgraded.Should().BeFalse();
    }

    [Fact]
    public void Standalone_5g_reports_its_own_mode()
    {
        var pts = Enumerable.Range(0, 12)
            .Select(i => Cell(ModeStart.AddMinutes(5 * i), "5G SA"))
            .ToList();

        var (_, mode, downgraded) =
            PhysicalLinkResolver.ResolveCellularMode(pts, Bucket, live: null);

        mode.Should().Be("5G SA");
        downgraded.Should().BeFalse();
    }

    [Fact]
    public void Empty_window_falls_back_to_the_live_snapshot()
    {
        var live = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -99 },
            Nr5g = new SignalInfo { Rsrp = -82 },
        };

        var (had5g, mode, downgraded) = PhysicalLinkResolver.ResolveCellularMode(
            new List<MonitoringInfluxClient.CellularPoint>(), Bucket, live);

        had5g.Should().BeTrue();
        mode.Should().Be("5G NSA");
        downgraded.Should().BeFalse();
    }
}
