using FluentAssertions;
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
}
