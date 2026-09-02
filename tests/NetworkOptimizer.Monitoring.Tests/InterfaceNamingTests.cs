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
    public void CompleteNamingAlwaysReplacesTheCache()
    {
        var cached = new InterfaceMetadataCache();
        var fresh = new InterfaceMetadataCache { NamingIncomplete = false };
        Assert.False(SnmpPoller.KeepCachedMetadata(cached, fresh));
    }
}
