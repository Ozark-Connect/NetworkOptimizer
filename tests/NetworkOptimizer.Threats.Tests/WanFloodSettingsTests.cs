using FluentAssertions;
using NetworkOptimizer.Threats.WanFlood;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class WanFloodSettingsTests
{
    [Fact]
    public async Task Defaults_AreOffWithTheValidatedLimits()
    {
        var s = await WanFloodSettings.LoadAsync(_ => Task.FromResult<string?>(null), null);
        s.Mode.Should().Be(WanFloodMode.Off);
        s.UdpPerSource.Should().Be(2000);
        s.UdpBurst.Should().Be(4000);
        s.TestDrop.Should().BeFalse();
    }

    [Fact]
    public async Task SiteKeys_AreSuffixedAndValuesClamped()
    {
        var store = new Dictionary<string, string>
        {
            ["wanflood.mode:branch"] = "Enforce",
            ["wanflood.udp_per_source:branch"] = "1",
            ["wanflood.drop_ttl:branch"] = "999999",
        };
        var s = await WanFloodSettings.LoadAsync(k => Task.FromResult(store.GetValueOrDefault(k)), "branch");
        s.Mode.Should().Be(WanFloodMode.Enforce);
        s.UdpPerSource.Should().Be(WanFloodSettings.MinRate);
        s.DropTtlSeconds.Should().Be(WanFloodSettings.MaxTtl);
    }

    [Fact]
    public void Clamped_KeepsTheLongestDropAtLeastTheDrop()
    {
        var s = new WanFloodSettings { DropTtlSeconds = 1200, MaxDropTtlSeconds = 600 }.Clamped();
        s.MaxDropTtlSeconds.Should().Be(1200);
    }

    [Fact]
    public void NeverDrop_KeepsOnlyAddressesAndRanges()
    {
        var s = new WanFloodSettings { NeverDrop = "203.0.113.5\n198.51.100.0/24, not-an-ip\n2001:db8::/32\n203.0.113.5" };
        s.NeverDropEntries().Should().BeEquivalentTo(["203.0.113.5", "198.51.100.0/24", "2001:db8::/32"]);
    }
}
