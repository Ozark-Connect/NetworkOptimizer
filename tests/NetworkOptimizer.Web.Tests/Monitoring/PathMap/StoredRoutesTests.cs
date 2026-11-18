using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.PathMap;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathMap;

public class StoredRoutesTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static StoredRoute Route(int minute, string? signature) =>
        new(T0.AddMinutes(minute), "server", null, "192.0.2.9", [new StoredRouteHop(1, "198.51.100.1", null, 1, 3, 3)], signature);

    private const string ViaRiverton = "Lakeview|64500 > Riverton|64501";
    private const string ViaSpringfield = "Lakeview|64500 > Springfield|64502";

    [Fact]
    public void Advance_FirstTrace_IsConfirmed()
    {
        var (state, shift) = StoredRoutes.Advance(null, Route(0, ViaRiverton));

        state.Stable.Signature.Should().Be(ViaRiverton);
        shift.Should().BeNull();
    }

    [Fact]
    public void Advance_OneDifferentTrace_IsNotAShift()
    {
        var (state, _) = StoredRoutes.Advance(null, Route(0, ViaRiverton));
        (state, var shift) = StoredRoutes.Advance(state, Route(1, ViaSpringfield));

        shift.Should().BeNull();
        state.Stable.Signature.Should().Be(ViaRiverton);
        state.Latest.Signature.Should().Be(ViaSpringfield);
    }

    [Fact]
    public void Advance_TwoAgreeingDifferentTraces_ConfirmAShiftFromTheFirst()
    {
        var (state, _) = StoredRoutes.Advance(null, Route(0, ViaRiverton));
        (state, _) = StoredRoutes.Advance(state, Route(1, ViaSpringfield));
        (state, var shift) = StoredRoutes.Advance(state, Route(2, ViaSpringfield));

        shift.Should().NotBeNull();
        shift!.At.Should().Be(T0.AddMinutes(1));
        shift.From.Signature.Should().Be(ViaRiverton);
        shift.To.Signature.Should().Be(ViaSpringfield);
        state.Stable.Signature.Should().Be(ViaSpringfield);
    }

    [Fact]
    public void Advance_AFlapBack_IsNotAShift()
    {
        var (state, _) = StoredRoutes.Advance(null, Route(0, ViaRiverton));
        (state, _) = StoredRoutes.Advance(state, Route(1, ViaSpringfield));
        (state, var shift) = StoredRoutes.Advance(state, Route(2, ViaRiverton));

        shift.Should().BeNull();
        state.Stable.Signature.Should().Be(ViaRiverton);
    }

    [Fact]
    public void Advance_UnplacedTrace_KeepsTheConfirmedRoute()
    {
        var (state, _) = StoredRoutes.Advance(null, Route(0, ViaRiverton));
        (state, var shift) = StoredRoutes.Advance(state, Route(1, null));

        shift.Should().BeNull();
        state.Stable.Signature.Should().Be(ViaRiverton);
    }

    [Fact]
    public void Differs_AHopThatStoppedAnswering_IsTheSameRoute()
    {
        RouteSignature.Differs("Lakeview|64500 > Riverton|64501 > Oakdale|64503", "Lakeview|64500 > Oakdale|64503")
            .Should().BeFalse();
    }

    [Fact]
    public void Differs_ASwappedNetwork_IsADifferentRoute()
    {
        RouteSignature.Differs(ViaRiverton, ViaSpringfield).Should().BeTrue();
    }

    [Fact]
    public void Build_CollapsesRepeatsAndSkipsUnknownHops()
    {
        RouteSignature.Build([("Lakeview", 64500), ("Lakeview", 64500), (null, null), (null, 64501)])
            .Should().Be("Lakeview|64500 > |64501");
    }

    [Fact]
    public void RouteAt_BeforeAShift_IsTheOldRoute()
    {
        var old = Route(0, ViaRiverton);
        var now = Route(10, ViaSpringfield);
        var state = new StoredRouteState(now, now, now);
        var shifts = new[] { new RouteShift(T0.AddMinutes(5), old, now) };

        StoredRoutes.RouteAt(state, shifts, T0.AddMinutes(3)).Signature.Should().Be(ViaRiverton);
        StoredRoutes.RouteAt(state, shifts, T0.AddMinutes(7)).Signature.Should().Be(ViaSpringfield);
    }

    [Fact]
    public void Append_KeepsTheNewestShifts()
    {
        IReadOnlyList<RouteShift> shifts = [];
        for (var i = 0; i < StoredRoutes.MaxShifts + 3; i++)
            shifts = StoredRoutes.Append(shifts, new RouteShift(T0.AddMinutes(i), Route(i, ViaRiverton), Route(i, ViaSpringfield)));

        shifts.Should().HaveCount(StoredRoutes.MaxShifts);
        shifts[^1].At.Should().Be(T0.AddMinutes(StoredRoutes.MaxShifts + 2));
    }

    [Fact]
    public void State_RoundTripsThroughJson()
    {
        var state = new StoredRouteState(Route(1, ViaSpringfield), Route(0, ViaRiverton), Route(1, ViaSpringfield));

        StoredRoutes.ReadState(StoredRoutes.Write(state))!.Latest.Hops[0].Address.Should().Be("198.51.100.1");
        StoredRoutes.ReadState("not json").Should().BeNull();
    }

    [Fact]
    public void Advance_ARouteThatOnlyLostAHop_KeepsItOnTheConfirmedRoute_Marked()
    {
        StoredRouteHop Hop(int ttl, string? address) => new(ttl, address, null, address == null ? null : ttl * 2.0, 3, address == null ? 0 : 3);
        // The router at hop 2 answers only some of the time; the destination rotates its address.
        var heard = new StoredRoute(T0, "server", null, "203.0.113.9", [Hop(1, "198.51.100.1"), Hop(2, "198.51.100.2"), Hop(3, "203.0.113.9")], "A > B");
        var quiet = new StoredRoute(T0.AddMinutes(1), "server", null, "203.0.113.50", [Hop(1, "198.51.100.1"), Hop(2, null), Hop(3, "203.0.113.50")], "A");

        var (state, _) = StoredRoutes.Advance(null, heard);
        (state, var shift) = StoredRoutes.Advance(state, quiet);

        shift.Should().BeNull();
        state.Latest.Hops[1].Address.Should().BeNull();
        state.Stable.Hops[1].Address.Should().Be("198.51.100.2");
        state.Stable.Hops[1].SeenAt.Should().Be(T0);
        state.Stable.Hops[2].Address.Should().Be("203.0.113.50");
    }
}
