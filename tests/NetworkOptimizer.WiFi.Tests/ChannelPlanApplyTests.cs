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
    public void FromPlan_ListsChangedRows_AndLeavesMeshChildrenToTheirParent()
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

        toApply.Should().Equal(
            new ChannelApplyItem("aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:01", RadioBand.Band6GHz, 101, 160, 133, 160, false),
            new ChannelApplyItem("aa:bb:cc:dd:ee:04", "aa:bb:cc:dd:ee:04", RadioBand.Band6GHz, 5, 160, 69, 320, false));
        notApplied.Should().ContainSingle().Which.Should().Be(new ChannelApplyOutcome(
            new ChannelApplyItem("aa:bb:cc:dd:ee:03", "aa:bb:cc:dd:ee:03", RadioBand.Band6GHz, 101, 160, 133, 160, false),
            ChannelApplyStatus.Skipped, "Follows its mesh parent"));
    }

    private static ChannelApplyItem Move(string mac, string name) =>
        new(mac, name, RadioBand.Band2_4GHz, 6, 20, 11, 20, false);

    private static Dictionary<string, HashSet<string>> Hearing(params (string A, string B)[] pairs)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (a, b) in pairs)
        {
            map.TryAdd(a, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            map.TryAdd(b, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            map[a].Add(b);
            map[b].Add(a);
        }
        return map;
    }

    [Fact]
    public void Waves_NoHearingMap_MovesOneApAtATime()
    {
        var items = new[] { Move("aa:bb:cc:dd:ee:01", "A"), Move("aa:bb:cc:dd:ee:02", "B") };

        ChannelPlanApply.Waves(items, null).Should().HaveCount(2).And.OnlyContain(w => w.Count == 1);
    }

    [Fact]
    public void Waves_ApsThatHearEachOtherNeverShareAWave()
    {
        // Four APs: Back Yard hears the other three, Main Kitchen and Front Yard hear each other,
        // and Tiny Home hears only Back Yard.
        var by = Move("aa:bb:cc:dd:ee:01", "Back Yard");
        var fy = Move("aa:bb:cc:dd:ee:02", "Front Yard");
        var mk = Move("aa:bb:cc:dd:ee:03", "Main Kitchen");
        var th = Move("aa:bb:cc:dd:ee:04", "Tiny Home");
        var hearing = Hearing((th.ApMac, by.ApMac), (mk.ApMac, fy.ApMac), (by.ApMac, fy.ApMac), (mk.ApMac, by.ApMac));

        var waves = ChannelPlanApply.Waves([th, mk, fy, by], hearing);

        waves.Should().HaveCount(3);
        waves[0].Should().Equal(by);
        waves[1].Should().BeEquivalentTo([fy, th]);
        waves[2].Should().Equal(mk);
    }

    [Fact]
    public void Waves_AnApMissingFromTheMap_MovesAlone()
    {
        var known = Move("aa:bb:cc:dd:ee:01", "Known");
        var other = Move("aa:bb:cc:dd:ee:02", "Other");
        var unknown = Move("aa:bb:cc:dd:ee:09", "Unknown");
        var hearing = Hearing();
        hearing[known.ApMac] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        hearing[other.ApMac] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var waves = ChannelPlanApply.Waves([known, other, unknown], hearing);

        waves.Should().HaveCount(2);
        waves.Should().ContainSingle(w => w.Count == 1 && w[0] == unknown);
        waves.Should().ContainSingle(w => w.Count == 2);
    }

    [Fact]
    public void Waves_ApsThatHearNoOne_AreCappedPerWave()
    {
        var items = Enumerable.Range(1, 12).Select(i => Move($"aa:bb:cc:dd:ee:{i:x2}", $"AP{i:00}")).ToList();
        var hearing = items.ToDictionary(i => i.ApMac, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var waves = ChannelPlanApply.Waves(items, hearing);

        waves.Select(w => w.Count).Should().Equal(ChannelPlanApply.MaxWaveSize, 12 - ChannelPlanApply.MaxWaveSize);
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
