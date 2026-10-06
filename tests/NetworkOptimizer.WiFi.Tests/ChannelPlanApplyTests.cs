using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class ChannelPlanApplyTests
{
    private const string ApMac = "aa:bb:cc:dd:ee:01";

    /// <summary>
    /// The stat/device fields the apply path reads, shaped like a real U7 response: radio_table
    /// out of band order (the console reorders it after a PUT), live channel in radio_table_stats.
    /// </summary>
    private static UniFiDeviceResponse Device(int state = 1, int sixGhzChannel = 101, int sixGhzBw = 160) =>
        JsonSerializer.Deserialize<UniFiDeviceResponse>($$"""
        {
          "_id": "0123456789abcdef01234567",
          "mac": "{{ApMac}}",
          "state": {{state}},
          "radio_table": [
            { "name": "wifi2", "radio": "6e", "channel": {{sixGhzChannel}}, "ht": {{sixGhzBw}} },
            { "name": "wifi0", "radio": "ng", "channel": "auto", "ht": 20 },
            { "name": "wifi1", "radio": "na", "channel": 100, "ht": 160 }
          ],
          "radio_table_stats": [
            { "name": "wifi0", "radio": "ng", "channel": 6, "bw": 20 },
            { "name": "wifi1", "radio": "na", "channel": 100, "bw": 160 },
            { "name": "wifi2", "radio": "6e", "channel": {{sixGhzChannel}}, "bw": {{sixGhzBw}} }
          ]
        }
        """)!;

    private static ChannelApplyItem Item(RadioBand band = RadioBand.Band6GHz, int current = 101, int currentWidth = 160,
        int target = 133, int width = 160) =>
        new(ApMac, "AP1", band, current, currentWidth, target, width, IsDfs: false);

    private static ApChannelRecommendation Rec(string mac, int current, int recommended, int width = 160,
        bool mesh = false, bool kept = false, int recommendedWidth = 0) => new()
    {
        ApMac = mac,
        ApName = mac,
        Band = RadioBand.Band6GHz,
        CurrentChannel = current,
        CurrentWidth = width,
        RecommendedChannel = recommended,
        RecommendedWidth = recommendedWidth == 0 ? width : recommendedWidth,
        IsMeshConstrained = mesh,
        IsKept = kept
    };

    [Fact]
    public void FromPlan_ListsChangedRows_AndLeavesMeshChildrenAnd320ForUniFi()
    {
        var plan = new ChannelPlan
        {
            Band = RadioBand.Band6GHz,
            Recommendations =
            {
                Rec("aa:bb:cc:dd:ee:01", 101, 133),
                Rec("aa:bb:cc:dd:ee:02", 37, 37),
                Rec("aa:bb:cc:dd:ee:03", 101, 133, mesh: true),
                Rec("aa:bb:cc:dd:ee:04", 5, 69, width: 160, recommendedWidth: 320),
                Rec("aa:bb:cc:dd:ee:05", 165, 165, kept: true)
            }
        };

        var (toApply, notApplied) = ChannelPlanApply.FromPlan(plan);

        toApply.Should().ContainSingle().Which.Should().Be(
            new ChannelApplyItem("aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:01", RadioBand.Band6GHz, 101, 160, 133, 160, false));
        notApplied.Select(o => o.Item.ApMac).Should().Equal("aa:bb:cc:dd:ee:03", "aa:bb:cc:dd:ee:04");
        notApplied.Should().OnlyContain(o => o.Status == ChannelApplyStatus.Skipped);
    }

    [Fact]
    public void Preflight_MatchingDevice_TargetsTheBandsRadioByName()
    {
        var (update, stop) = ChannelPlanApply.Preflight(Item(), Device());

        stop.Should().BeNull();
        update.Should().Be(new RadioChannelUpdate("wifi2", "6e", 133, 160));
    }

    [Fact]
    public void Preflight_AutoChannelRadio_ComparesTheLiveChannel()
    {
        var (update, stop) = ChannelPlanApply.Preflight(
            Item(RadioBand.Band2_4GHz, current: 6, currentWidth: 20, target: 1, width: 20), Device());

        stop.Should().BeNull();
        update.Should().Be(new RadioChannelUpdate("wifi0", "ng", 1, 20));
    }

    [Fact]
    public void Preflight_MissingDevice_Fails()
    {
        var (update, stop) = ChannelPlanApply.Preflight(Item(), null);

        update.Should().BeNull();
        stop!.Status.Should().Be(ChannelApplyStatus.Failed);
    }

    [Fact]
    public void Preflight_OfflineDevice_Skips()
    {
        var (update, stop) = ChannelPlanApply.Preflight(Item(), Device(state: 0));

        update.Should().BeNull();
        stop!.Status.Should().Be(ChannelApplyStatus.Skipped);
        stop.Reason.Should().Be("Offline");
    }

    [Theory]
    [InlineData(37, 160)]
    [InlineData(101, 80)]
    public void Preflight_RadioMovedSinceThePlan_Skips(int liveChannel, int liveWidth)
    {
        var (update, stop) = ChannelPlanApply.Preflight(Item(), Device(sixGhzChannel: liveChannel, sixGhzBw: liveWidth));

        update.Should().BeNull();
        stop!.Status.Should().Be(ChannelApplyStatus.Skipped);
        stop.Reason.Should().Contain($"Ch {liveChannel} / {liveWidth} MHz");
    }

    [Fact]
    public void HasArrived_OnlyOnceTheLiveRadioReportsTheTarget()
    {
        ChannelPlanApply.HasArrived(Item(), Device(sixGhzChannel: 101)).Should().BeFalse();
        ChannelPlanApply.HasArrived(Item(), Device(sixGhzChannel: 133)).Should().BeTrue();
        ChannelPlanApply.HasArrived(Item(), Device(state: 5, sixGhzChannel: 133)).Should().BeFalse();
    }
}
