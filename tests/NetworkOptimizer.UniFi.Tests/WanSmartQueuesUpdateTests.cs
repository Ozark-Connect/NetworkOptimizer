using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class WanSmartQueuesUpdateTests
{
    private const string NetworkConf = """
        {"meta":{"rc":"ok"},"data":[
          {"_id":"lan1","purpose":"corporate","name":"LAN","wan_networkgroup":"WAN"},
          {"_id":"wan1","purpose":"wan","name":"Fiber","wan_networkgroup":"WAN","wan_smartq_enabled":true,
           "wan_smartq_down_rate":1077000,"wan_smartq_up_rate":999000,"wan_vlan":201,"x_unknown":{"keep":[1,2]}},
          {"_id":"wan2","purpose":"wan","name":"Cable","wan_networkgroup":"WAN2","wan_smartq_enabled":true,
           "wan_smartq_down_rate":"274000","wan_smartq_up_rate":29000},
          {"_id":"wan3","purpose":"wan","name":"Cellular","wan_networkgroup":"WAN3","wan_smartq_enabled":false}
        ]}
        """;

    [Fact]
    public void SetsOnlyTheGivenFields_OnTheWanForTheGroup_AndKeepsEveryOtherField()
    {
        var update = UniFiApiClient.BuildSmartQueuesUpdate(NetworkConf, "WAN", null, 1000000, null);

        update.Error.Should().BeNull();
        update.Id.Should().Be("wan1", "a LAN network carrying the same group is not a WAN");
        update.Previous.Should().Be(new WanSmartQueues(true, 1077000, 999000));
        var body = JsonNode.Parse(update.Body!)!;
        ((int)body["wan_smartq_down_rate"]!).Should().Be(1000000);
        ((int)body["wan_smartq_up_rate"]!).Should().Be(999000);
        ((bool)body["wan_smartq_enabled"]!).Should().BeTrue();
        ((int)body["wan_vlan"]!).Should().Be(201);
        body["x_unknown"]!["keep"]!.AsArray().Count.Should().Be(2, "fields the typed model lacks go back unchanged");
    }

    [Fact]
    public void ReadsARateSentAsAString()
    {
        var update = UniFiApiClient.BuildSmartQueuesUpdate(NetworkConf, "WAN2", false, null, null);

        update.Id.Should().Be("wan2");
        update.Previous!.DownloadKbps.Should().Be(274000);
        ((bool)JsonNode.Parse(update.Body!)!["wan_smartq_enabled"]!).Should().BeFalse();
    }

    [Fact]
    public void TurnsSmartQueuesOn_OnlyWithBothRates()
    {
        UniFiApiClient.BuildSmartQueuesUpdate(NetworkConf, "WAN3", true, 100000, null).Error
            .Should().Contain("both a download and an upload rate");

        var update = UniFiApiClient.BuildSmartQueuesUpdate(NetworkConf, "WAN3", true, 100000, 20000);
        update.Error.Should().BeNull();
        update.Previous.Should().Be(new WanSmartQueues(false, null, null));
    }

    [Theory]
    [InlineData("WAN4", 1000, "no WAN network")]
    [InlineData("WAN", 0, "above zero")]
    [InlineData("WAN", -5, "above zero")]
    public void RefusesAMissingWanOrARateThatIsNotPositive(string group, int downloadKbps, string error)
    {
        UniFiApiClient.BuildSmartQueuesUpdate(NetworkConf, group, null, downloadKbps, null).Error.Should().Contain(error);
    }

    [Fact]
    public void RefusesAnAmbiguousNetworkGroup()
    {
        var twice = NetworkConf.Replace("\"wan_networkgroup\":\"WAN2\"", "\"wan_networkgroup\":\"WAN\"");

        UniFiApiClient.BuildSmartQueuesUpdate(twice, "WAN", null, 1000, null).Error.Should().Contain("more than one");
    }

    [Fact]
    public void RefusesAResponseThatIsNotJson()
    {
        UniFiApiClient.BuildSmartQueuesUpdate("<html>", "WAN", null, 1000, null).Error.Should().Contain("could not be read");
    }
}
