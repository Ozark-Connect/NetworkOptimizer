using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>A chip editor's rows stay where they are while it is open: ticks and refreshes never move them.</summary>
public class FacetRowOrderTests
{
    private static LiveFacetValue F(string v, int n) => new(v, n);
    private static LiveFacetValue Hidden(string v) => new(v, 0, Hidden: true);
    private static LiveFacetValue NoTraffic(string v) => new(v, 0, Idle: true);
    private static LiveFacetValue Offline(string v) => new(v, 0, Idle: true, Offline: true);

    [Fact]
    public void OpensWithTheChipsValuesThenByCountThenTheMutedSections()
    {
        var order = new FacetRowOrder();
        order.Arrange(["Gateway"], [Offline("Thermostat"), F("Archipelago", 37), Hidden("Printer"), F("Gateway", 30), NoTraffic("TV")])
            .Should().Equal("Gateway", "Archipelago", "Printer", "TV", "Thermostat");
        order.RankOf("Printer").Should().Be(1);
        order.RankOf("TV").Should().Be(2);
        order.RankOf("Thermostat").Should().Be(3);
    }

    [Fact]
    public void TickingAndNewCountsLeaveEveryRowInPlace()
    {
        var order = new FacetRowOrder();
        order.Arrange(["Gateway"], [F("Archipelago", 37), F("Gateway", 30), F("192.0.2.9", 1), F("198.51.100.9", 1)]);

        // A tick, then counts that would sort differently: nothing moves.
        order.Arrange(["Gateway", "192.0.2.9"], [F("198.51.100.9", 50), F("Archipelago", 38), F("Gateway", 30), F("192.0.2.9", 1)])
            .Should().Equal("Gateway", "Archipelago", "192.0.2.9", "198.51.100.9");

        // Unticking, and a value that left the counts, keep their rows too.
        order.Arrange([], [F("Archipelago", 38)])
            .Should().Equal("Gateway", "Archipelago", "192.0.2.9", "198.51.100.9");
    }

    [Fact]
    public void ALaterValueJoinsTheEndOfItsOwnSection()
    {
        var order = new FacetRowOrder();
        order.Arrange([], [F("Laptop", 10), Hidden("Printer"), Offline("Thermostat")]);

        order.Arrange([], [F("Laptop", 10), F("TV", 3), Hidden("Printer"), Hidden("NAS"), Offline("Thermostat")])
            .Should().Equal("Laptop", "TV", "Printer", "NAS", "Thermostat");
    }
}
