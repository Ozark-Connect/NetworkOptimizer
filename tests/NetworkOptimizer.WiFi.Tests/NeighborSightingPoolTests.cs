using FluentAssertions;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class NeighborSightingPoolTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddHours(1);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static (NeighborNetwork, DateTimeOffset) Sighting(string bssid, int channel, int signal, TimeSpan ago)
        => (new NeighborNetwork { Bssid = bssid, Channel = channel, Signal = signal }, Now - ago);

    [Fact]
    public void Union_NeighborMissingFromLatestScan_HeldWhileWithinWindow()
    {
        // Seen 3 minutes ago, absent from the latest scan - must still count.
        var pooled = NeighborSightingPool.Union(
            new[] { Sighting("aa:bb:cc:00:00:01", 1, -60, TimeSpan.FromMinutes(3)) },
            Now, Window);

        pooled.Should().ContainSingle(n => n.Bssid == "aa:bb:cc:00:00:01");
    }

    [Fact]
    public void Union_NeighborAbsentBeyondWindow_AgesOut()
    {
        var pooled = NeighborSightingPool.Union(
            new[] { Sighting("aa:bb:cc:00:00:01", 1, -60, TimeSpan.FromMinutes(15)) },
            Now, Window);

        pooled.Should().BeEmpty();
    }

    [Fact]
    public void Union_SameBssidMultipleSightings_KeepsStrongestSignal()
    {
        var pooled = NeighborSightingPool.Union(
            new[]
            {
                Sighting("aa:bb:cc:00:00:01", 1, -75, TimeSpan.FromMinutes(5)),
                Sighting("aa:bb:cc:00:00:01", 1, -55, TimeSpan.FromMinutes(2)),
                Sighting("aa:bb:cc:00:00:01", 1, -80, TimeSpan.Zero)
            },
            Now, Window);

        pooled.Should().ContainSingle();
        pooled[0].Signal.Should().Be(-55);
    }

    [Fact]
    public void Union_DistinctBssids_AllRetained()
    {
        var pooled = NeighborSightingPool.Union(
            new[]
            {
                Sighting("aa:bb:cc:00:00:01", 1, -60, TimeSpan.Zero),
                Sighting("aa:bb:cc:00:00:02", 6, -65, TimeSpan.FromMinutes(4))
            },
            Now, Window);

        pooled.Should().HaveCount(2);
    }

    [Fact]
    public void Union_EmptyBssid_Ignored()
    {
        var pooled = NeighborSightingPool.Union(
            new[] { Sighting("", 1, -60, TimeSpan.Zero) },
            Now, Window);

        pooled.Should().BeEmpty();
    }
}
