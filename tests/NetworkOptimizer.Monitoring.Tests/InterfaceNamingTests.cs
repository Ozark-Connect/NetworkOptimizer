using NetworkOptimizer.Monitoring;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// How an interface gets its name, and what a failed naming walk may not do to it. The name is
/// the Influx series key and the name-map key, so a transient SNMP failure that renamed a port
/// split its history and left a second row behind.
/// </summary>
public class InterfaceNamingTests
{
    [Fact]
    public void AliasNamesASwitchPort()
    {
        Assert.Equal("NE Camera Port", SnmpPoller.ResolveIfName("NE Camera Port", "0/2"));
    }

    [Fact]
    public void RawNameStandsWhenThereIsNoAlias()
    {
        Assert.Equal("0/2", SnmpPoller.ResolveIfName(null, "0/2"));
    }

    [Fact]
    public void GatewayEthPortKeepsItsRawNameOverALabel()
    {
        Assert.Equal("eth6", SnmpPoller.ResolveIfName("Fiber", "eth6"));
    }

    [Fact]
    public void IncompleteNamingIsDroppedWhenMetadataIsCached()
    {
        var cached = new InterfaceMetadataCache();
        var fresh = new InterfaceMetadataCache { NamingIncomplete = true };
        Assert.True(SnmpPoller.KeepCachedMetadata(cached, fresh));
    }

    [Fact]
    public void IncompleteNamingIsAdoptedWhenNothingIsCachedYet()
    {
        var fresh = new InterfaceMetadataCache { NamingIncomplete = true };
        Assert.False(SnmpPoller.KeepCachedMetadata(null, fresh));
    }

    [Fact]
    public void MergeKeepsTheCachedNamesAndTakesTheFreshSpeeds()
    {
        var cached = new InterfaceMetadataCache
        {
            NameByIdx = { ["2"] = "NE Camera Port" },
            AliasByIdx = { ["2"] = "NE Camera Port" },
            HighSpeedByIdx = { ["2"] = "1000" },
        };
        var fresh = new InterfaceMetadataCache
        {
            NameByIdx = { ["2"] = "0/2" },
            HighSpeedByIdx = { ["2"] = "100" },
            AdminByIdx = { ["2"] = "1" },
            NamingIncomplete = true,
        };

        var merged = SnmpPoller.MergeKeepingNames(cached, fresh);

        Assert.Equal("NE Camera Port", merged.NameByIdx["2"]);
        Assert.Equal("NE Camera Port", merged.AliasByIdx["2"]);
        Assert.Equal("100", merged.HighSpeedByIdx["2"]);
        Assert.Equal("1", merged.AdminByIdx["2"]);
        Assert.False(merged.NamingIncomplete);
    }

    [Fact]
    public void MergeStandsOnTheCacheWhenThePartialWalkMisreadTheIndexOffset()
    {
        var cached = new InterfaceMetadataCache { IfXTableIndexOffset = 1_000_000, NameByIdx = { ["1"] = "Uplink" } };
        var fresh = new InterfaceMetadataCache { IfXTableIndexOffset = 0, NamingIncomplete = true };

        Assert.Same(cached, SnmpPoller.MergeKeepingNames(cached, fresh));
    }

    [Fact]
    public void CompleteNamingAlwaysReplacesTheCache()
    {
        var cached = new InterfaceMetadataCache();
        var fresh = new InterfaceMetadataCache { NamingIncomplete = false };
        Assert.False(SnmpPoller.KeepCachedMetadata(cached, fresh));
    }
}
