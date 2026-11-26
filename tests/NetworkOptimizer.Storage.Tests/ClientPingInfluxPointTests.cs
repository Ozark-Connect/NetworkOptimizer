using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>A Client's live pings are added fields on its own wifi_client or wired_client series.</summary>
public class ClientPingInfluxPointTests
{
    private static readonly DateTime At = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AWiFiClientsReplyLandsOnItsAccessPointAndBand()
    {
        var line = MonitoringInfluxClient.BuildClientPingPoint("AA-BB-CC-00-00-02", false, "aa:bb:cc:00:00:01", "5GHZ", null,
            0.42, "Network Optimizer server", At)!.ToLineProtocol();

        // The client is a tag, never the client_mac field the throughput and signal readers pivot on.
        line.Should().StartWith("wifi_client,band=5ghz,client_mac=aa:bb:cc:00:00:02,device_mac=aa:bb:cc:00:00:01 ")
            .And.NotContain("client_mac=\"").And.Contain("ping_received=1i")
            .And.Contain("ping_rtt_ms=0.42").And.Contain("ping_vantage=\"Network Optimizer server\"");
    }

    [Fact]
    public void AWiredClientsNoReplyLandsOnItsSwitchPortWithNoRoundTrip()
    {
        var line = MonitoringInfluxClient.BuildClientPingPoint("aa:bb:cc:00:00:02", true, "aa:bb:cc:00:00:01", null, 6,
            null, "Agent A (On-Site Agent)", At)!.ToLineProtocol();

        line.Should().StartWith("wired_client,client_mac=aa:bb:cc:00:00:02,device_mac=aa:bb:cc:00:00:01,port=6 ")
            .And.NotContain("client_mac=\"").And.Contain("ping_received=0i").And.NotContain("ping_rtt_ms");
    }

    [Fact]
    public void NoParentOrNoWiFiBandWritesNothing()
    {
        MonitoringInfluxClient.BuildClientPingPoint("aa:bb:cc:00:00:02", true, "", null, 6, 1.0, "x", At).Should().BeNull();
        MonitoringInfluxClient.BuildClientPingPoint("aa:bb:cc:00:00:02", false, "aa:bb:cc:00:00:01", null, null, 1.0, "x", At).Should().BeNull();
    }
}
