using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.PathMap;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathMap;

public class HopHistoryTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static StoredRoute Route(DateTime at, string vantage, params string?[] addresses) =>
        new(at, vantage, null, addresses[^1],
            addresses.Select((a, i) => new StoredRouteHop(i + 1, a, null, a == null ? null : 2.0 * (i + 1), 3, a == null ? 0 : 3)).ToList(),
            null);

    [Fact]
    public void Fill_SilentHopBetweenTheSameNeighbors_TakesTheAddressAnEarlierTraceSaw()
    {
        var now = Route(Now, "server", "198.51.100.1", null, "203.0.113.9");
        var earlier = Route(Now.AddMinutes(-30), "server", "198.51.100.1", "198.51.100.2", "203.0.113.9");

        var filled = HopHistory.Fill(now, [earlier]);

        filled.Hops[1].Address.Should().Be("198.51.100.2");
        filled.Hops[1].SeenAt.Should().Be(earlier.CheckedAt);
        filled.Hops[0].SeenAt.Should().BeNull();
    }

    [Fact]
    public void Fill_DifferentNeighborOrVantage_LeavesItSilent()
    {
        var now = Route(Now, "server", "198.51.100.1", null, "203.0.113.9");
        var otherAfter = Route(Now.AddMinutes(-30), "server", "198.51.100.1", "198.51.100.2", "203.0.113.50");
        var otherVantage = Route(Now.AddMinutes(-30), "agent-1", "198.51.100.1", "198.51.100.2", "203.0.113.9");

        HopHistory.Fill(now, [otherAfter, otherVantage]).Hops[1].Address.Should().BeNull();
    }

    [Fact]
    public void Fill_SilentTail_IsLeftSilent()
    {
        var now = Route(Now, "server", "198.51.100.1", null, null);
        var earlier = Route(Now.AddMinutes(-30), "server", "198.51.100.1", "198.51.100.2", "203.0.113.9");

        HopHistory.Fill(now, [earlier]).Hops.Skip(1).Should().OnlyContain(h => h.Address == null);
    }

    [Fact]
    public void Fill_NewestEarlierTraceWins()
    {
        var now = Route(Now, "server", "198.51.100.1", null, "203.0.113.9");
        var old = Route(Now.AddHours(-2), "server", "198.51.100.1", "198.51.100.2", "203.0.113.9");
        var recent = Route(Now.AddMinutes(-5), "server", "198.51.100.1", "198.51.100.3", "203.0.113.9");

        HopHistory.Fill(now, [old, recent]).Hops[1].Address.Should().Be("198.51.100.3");
    }

    [Fact]
    public void Fill_GapBeforeTheDestination_MatchesAnotherTraceToADestinationOnTheSameNetwork()
    {
        // A hostname target rotates its address: the hop after the gap differs, but both are the destination on one network.
        var now = Route(Now, "server", "198.51.100.1", null, "203.0.113.9");
        var other = Route(Now.AddMinutes(-10), "server", "198.51.100.1", "198.51.100.2", "203.0.113.50");

        HopHistory.Fill(now, [other], a => a.StartsWith("203.0.113.") ? 64500 : 64501).Hops[1].Address.Should().Be("198.51.100.2");
        HopHistory.Fill(now, [other]).Hops[1].Address.Should().BeNull();
        HopHistory.Fill(now, [other], a => a == "203.0.113.9" ? 64500 : 64502).Hops[1].Address.Should().BeNull();
    }

    [Fact]
    public void Fill_ASightingOlderThanADay_IsNotUsed()
    {
        var now = Route(Now, "server", "198.51.100.1", null, "203.0.113.9");
        var old = Route(Now.AddDays(-2), "server", "198.51.100.1", "198.51.100.2", "203.0.113.9");

        HopHistory.Fill(now, [old]).Hops[1].Address.Should().BeNull();
    }
}
