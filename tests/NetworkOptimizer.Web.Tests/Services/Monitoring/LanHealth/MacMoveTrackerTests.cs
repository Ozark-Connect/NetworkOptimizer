using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.LanHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.Monitoring.LanHealth;

/// <summary>MAC moves between switch ports across topology reads, for MAC Flapping.</summary>
public class MacMoveTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyCollection<string> NoPath = [];
    private static readonly IReadOnlyCollection<string> BelowGatewayPort4 = ["Gateway Port 4"];

    [Fact]
    public void CountsAMoveEachTimeAClientChangesPort()
    {
        var t = new MacMoveTracker();
        t.Observe(T0, [("AA:BB:CC:00:00:50", "NAS", "Core Port 3", NoPath), ("aa:bb:cc:00:00:51", "PC", "Core Port 4", NoPath)]);
        t.Observe(T0.AddMinutes(2), [("aa:bb:cc:00:00:50", "NAS", "Edge Port 7", NoPath), ("aa:bb:cc:00:00:51", "PC", "Core Port 4", NoPath)]);
        t.Observe(T0.AddMinutes(4), [("aa:bb:cc:00:00:50", "NAS", "Core Port 3", NoPath)]);
        t.Observe(T0.AddMinutes(6), [("aa:bb:cc:00:00:50", "NAS", "Edge Port 7", NoPath)]);
        var m = t.Since(T0.AddHours(-1)).Single();
        m.Name.Should().Be("NAS");
        m.Moves.Should().Be(3);
        m.Ports.Should().Equal("Core Port 3", "Edge Port 7");
        t.Since(T0.AddMinutes(5)).Single().Moves.Should().Be(1, "the window starts later");
    }

    [Fact]
    public void ForgetsMovesAfterTwoDays()
    {
        var t = new MacMoveTracker();
        t.Observe(T0, [("aa:bb:cc:00:00:50", "NAS", "a", NoPath)]);
        t.Observe(T0.AddMinutes(1), [("aa:bb:cc:00:00:50", "NAS", "b", NoPath)]);
        t.Observe(T0.AddHours(49), [("aa:bb:cc:00:00:50", "NAS", "b", NoPath)]);
        t.Since(T0.AddHours(-1)).Should().BeEmpty();
    }

    [Fact]
    public void AnUplinkOnTheClientsOwnPathIsNotAMove()
    {
        // The console alternates between the access port and the gateway port that feeds its switch.
        var t = new MacMoveTracker();
        for (var i = 0; i < 8; i++)
            t.Observe(T0.AddMinutes(i), [i % 2 == 0
                ? ("aa:bb:cc:00:00:52", "PC", "Office Port 2", BelowGatewayPort4)
                : ("aa:bb:cc:00:00:52", "PC", "Gateway Port 4", NoPath)]);
        t.Since(T0.AddHours(-1)).Should().BeEmpty();
    }

    [Fact]
    public void AFirstSightingOnTheUplinkSettlesOnTheAccessPortWithoutAMove()
    {
        var t = new MacMoveTracker();
        t.Observe(T0, [("aa:bb:cc:00:00:52", "PC", "Gateway Port 4", NoPath)]);
        t.Observe(T0.AddMinutes(1), [("aa:bb:cc:00:00:52", "PC", "Office Port 2", BelowGatewayPort4)]);
        t.Observe(T0.AddMinutes(2), [("aa:bb:cc:00:00:52", "PC", "Gateway Port 4", NoPath)]);
        t.Observe(T0.AddMinutes(3), [("aa:bb:cc:00:00:52", "PC", "Desk Port 5", ["Gateway Port 6"])]);
        var m = t.Since(T0.AddHours(-1)).Single();
        m.Moves.Should().Be(1, "only the move to a port off the old path counts");
        m.Ports.Should().Equal("Office Port 2", "Desk Port 5");
    }
}
