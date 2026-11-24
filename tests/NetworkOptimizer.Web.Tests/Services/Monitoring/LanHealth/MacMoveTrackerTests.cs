using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.LanHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.Monitoring.LanHealth;

/// <summary>MAC moves between switch ports across topology reads, for MAC Flapping.</summary>
public class MacMoveTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CountsAMoveEachTimeAClientChangesPort()
    {
        var t = new MacMoveTracker();
        t.Observe(T0, [("AA:BB:CC:00:00:50", "NAS", "Core Port 3"), ("aa:bb:cc:00:00:51", "PC", "Core Port 4")]);
        t.Observe(T0.AddMinutes(2), [("aa:bb:cc:00:00:50", "NAS", "Edge Port 7"), ("aa:bb:cc:00:00:51", "PC", "Core Port 4")]);
        t.Observe(T0.AddMinutes(4), [("aa:bb:cc:00:00:50", "NAS", "Core Port 3")]);
        t.Observe(T0.AddMinutes(6), [("aa:bb:cc:00:00:50", "NAS", "Edge Port 7")]);
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
        t.Observe(T0, [("aa:bb:cc:00:00:50", "NAS", "a")]);
        t.Observe(T0.AddMinutes(1), [("aa:bb:cc:00:00:50", "NAS", "b")]);
        t.Observe(T0.AddHours(49), [("aa:bb:cc:00:00:50", "NAS", "b")]);
        t.Since(T0.AddHours(-1)).Should().BeEmpty();
    }
}