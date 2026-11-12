using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>A chip editor's rows stay where they are while it is open: ticks and refreshes never move them.</summary>
public class FacetRowOrderTests
{
    private static LiveFacetValue F(string v, int n, bool idle = false) => new(v, n, idle);

    [Fact]
    public void OpensWithTheChipsValuesThenByCountThenIdle()
    {
        var order = new FacetRowOrder();
        order.Arrange(["Gateway"], [F("Archipelago", 37), F("Gateway", 30), F("192.0.2.9", 1), F("Thermostat", 0, idle: true)])
            .Should().Equal("Gateway", "Archipelago", "192.0.2.9", "Thermostat");
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
    public void ALaterValueGoesAboveTheIdleClients()
    {
        var order = new FacetRowOrder();
        order.Arrange([], [F("Laptop", 10), F("Thermostat", 0, idle: true)]);

        order.Arrange([], [F("Laptop", 10), F("TV", 3), F("Thermostat", 0, idle: true)])
            .Should().Equal("Laptop", "TV", "Thermostat");
        order.WasIdle("Thermostat").Should().BeTrue();
    }
}
