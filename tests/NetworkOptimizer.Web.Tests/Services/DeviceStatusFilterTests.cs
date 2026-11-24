using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class DeviceStatusFilterTests
{
    private static DeviceInfo Device(DeviceType type, string name = "Device", string ip = "192.0.2.10",
        string? model = null, string? firmware = null) =>
        new() { Type = type, Name = name, IpAddress = ip, Model = model, Firmware = firmware, Mac = "aa:bb:cc:dd:ee:ff" };

    [Theory]
    [InlineData("AP")]
    [InlineData("ap")]
    [InlineData("access point")]
    [InlineData("accesspoint")]
    [InlineData("wifi")]
    [InlineData("Wi-Fi")]
    [InlineData("wireless")]
    public void AccessPoint_MatchesItsSynonyms(string query)
    {
        DeviceStatusFilter.Matches(Device(DeviceType.AccessPoint), null, query).Should().BeTrue();
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("router")]
    [InlineData("firewall")]
    [InlineData("gw")]
    public void Gateway_MatchesItsSynonyms(string query)
    {
        DeviceStatusFilter.Matches(Device(DeviceType.Gateway), null, query).Should().BeTrue();
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("sw")]
    [InlineData("Sw")]
    public void Switch_MatchesItsSynonyms(string query)
    {
        DeviceStatusFilter.Matches(Device(DeviceType.Switch), null, query).Should().BeTrue();
    }

    [Theory]
    [InlineData("router")]
    [InlineData("ap")]
    [InlineData("wifi")]
    public void Switch_DoesNotMatchOtherTypesWords(string query)
    {
        DeviceStatusFilter.Matches(Device(DeviceType.Switch, name: "Rack"), null, query).Should().BeFalse();
    }

    [Theory]
    [InlineData("kitchen")]
    [InlineData("192.0.2")]
    [InlineData(".10")]
    [InlineData("u7")]
    [InlineData("8.1.113")]
    [InlineData("aa:bb")]
    [InlineData("online")]
    public void Fields_MatchBySubstring(string query)
    {
        var d = Device(DeviceType.AccessPoint, name: "Main Kitchen", model: "U7 Pro", firmware: "8.1.113");
        DeviceStatusFilter.Matches(d, null, query).Should().BeTrue();
    }

    [Fact]
    public void EveryWord_MustMatch()
    {
        var d = Device(DeviceType.AccessPoint, name: "Main Kitchen", model: "U7 Pro");
        DeviceStatusFilter.Matches(d, null, "kitchen ap").Should().BeTrue();
        DeviceStatusFilter.Matches(d, null, "kitchen switch").Should().BeFalse();
    }

    [Fact]
    public void TypeTab_NarrowsBeforeSearch()
    {
        var d = Device(DeviceType.Switch, name: "Office");
        DeviceStatusFilter.Matches(d, DeviceType.Switch, null).Should().BeTrue();
        DeviceStatusFilter.Matches(d, DeviceType.AccessPoint, null).Should().BeFalse();
        DeviceStatusFilter.Matches(d, DeviceType.Switch, "office").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQuery_MatchesEverything(string? query)
    {
        DeviceStatusFilter.Matches(Device(DeviceType.Unknown), null, query).Should().BeTrue();
    }

    [Theory]
    [InlineData(DeviceStatusKind.Online, false)]
    [InlineData(DeviceStatusKind.Transitional, false)]
    [InlineData(DeviceStatusKind.Offline, true)]
    [InlineData(DeviceStatusKind.Error, true)]
    public void NeedsAttention_FollowsStatusKind(DeviceStatusKind kind, bool expected)
    {
        var d = Device(DeviceType.Switch);
        d.StatusInfo = new DeviceStatus(kind, kind.ToString());
        DeviceStatusFilter.NeedsAttention(d, restartFailed: false).Should().Be(expected);
    }

    [Fact]
    public void NeedsAttention_AFailedRestartCounts()
    {
        DeviceStatusFilter.NeedsAttention(Device(DeviceType.Switch), restartFailed: true).Should().BeTrue();
    }

    [Theory]
    [InlineData(true, 3_600, true)]
    [InlineData(true, 2 * 86_400, false)]
    [InlineData(false, 3_600, false)]
    public void NeedsAttention_AnUnexpectedRebootCountsForADay(bool unexpected, long uptimeSeconds, bool expected)
    {
        var d = Device(DeviceType.AccessPoint);
        d.RebootWasUnexpected = unexpected;
        d.UptimeSeconds = uptimeSeconds;
        DeviceStatusFilter.NeedsAttention(d, restartFailed: false).Should().Be(expected);
    }

    private static List<DeviceInfo> Site()
    {
        var down = Device(DeviceType.Switch, name: "Office Switch");
        down.StatusInfo = new DeviceStatus(DeviceStatusKind.Offline, "Offline");
        var rebooted = Device(DeviceType.AccessPoint, name: "Back Yard AP");
        rebooted.RebootWasUnexpected = true;
        rebooted.UptimeSeconds = 600;
        return
        [
            Device(DeviceType.Gateway, name: "Gateway"), Device(DeviceType.Switch, name: "Core Switch"), down,
            Device(DeviceType.AccessPoint, name: "Kitchen AP"), rebooted,
        ];
    }

    private static bool Attn(DeviceInfo d) => DeviceStatusFilter.NeedsAttention(d, restartFailed: false);

    private static IEnumerable<string> Names(IEnumerable<DeviceInfo> ds) => ds.Select(d => d.Name);

    [Fact]
    public void Visible_NothingPicked_ShowsEverything()
    {
        Names(DeviceStatusFilter.Visible(Site(), null, "", false, Attn)).Should().HaveCount(5);
    }

    [Fact]
    public void Visible_NeedsAttention_ShowsOnlyThoseDevices()
    {
        Names(DeviceStatusFilter.Visible(Site(), null, "", true, Attn))
            .Should().BeEquivalentTo("Office Switch", "Back Yard AP");
    }

    [Fact]
    public void Visible_NeedsAttention_CombinesWithTypeAndSearch()
    {
        Names(DeviceStatusFilter.Visible(Site(), DeviceType.AccessPoint, "", true, Attn)).Should().Equal("Back Yard AP");
        Names(DeviceStatusFilter.Visible(Site(), null, "office", true, Attn)).Should().Equal("Office Switch");
        DeviceStatusFilter.Visible(Site(), DeviceType.Gateway, "", true, Attn).Should().BeEmpty();
    }

    [Fact]
    public void Visible_NeedsAttentionStopsFiltering_OnceNothingNeedsIt()
    {
        var healthy = Site().Where(d => !Attn(d)).ToList();
        DeviceStatusFilter.Visible(healthy, null, "", true, Attn).Should().HaveCount(healthy.Count);
    }

    [Fact]
    public void Visible_ATypeTheSiteLost_ReadsAsAll()
    {
        var noGateway = Site().Where(d => d.Type != DeviceType.Gateway).ToList();
        DeviceStatusFilter.EffectiveType(noGateway, DeviceType.Gateway).Should().BeNull();
        DeviceStatusFilter.Visible(noGateway, DeviceType.Gateway, "", false, Attn).Should().HaveCount(noGateway.Count);
    }

    [Fact]
    public void Search_HiddenAndIgnored_WithOneDevice()
    {
        var one = new List<DeviceInfo> { Device(DeviceType.Gateway, name: "Gateway") };
        DeviceStatusFilter.ShowsSearch(one).Should().BeFalse();
        DeviceStatusFilter.ShowsSearch(Site()).Should().BeTrue();
        // Text typed while a second device was there must not hide the last one.
        DeviceStatusFilter.Visible(one, null, "nomatch", false, Attn).Should().HaveCount(1);
    }

    [Fact]
    public void Visible_UsesTheCallersAttentionRule()
    {
        // The page folds a failed restart in through the callback.
        Names(DeviceStatusFilter.Visible(Site(), null, "", true, d => d.Name == "Gateway")).Should().Equal("Gateway");
    }

    public static TheoryData<DeviceType> AllTypes()
    {
        var data = new TheoryData<DeviceType>();
        foreach (var t in Enum.GetValues<DeviceType>()) data.Add(t);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void EveryType_HasATab_AndMatchesItsOwnName(DeviceType type)
    {
        // A name with nothing type-like in it, so only the type words can match.
        var d = Device(type, name: "Zz", ip: "", model: null, firmware: null);
        d.Mac = "";
        var others = Device(type == DeviceType.Gateway ? DeviceType.Switch : DeviceType.Gateway);

        DeviceStatusFilter.PresentTypes([d, others]).Select(t => t.Type).Should().Contain(type);
        DeviceStatusFilter.Visible([d, others], type, "", false, _ => false).Should().Equal(d);
        DeviceStatusFilter.Matches(d, null, type.ToDisplayName()).Should().BeTrue();
        DeviceStatusFilter.Matches(d, null, type.ToDisplayName().ToLowerInvariant()).Should().BeTrue();
    }

    [Theory]
    [InlineData(DeviceType.SmartPower, "power")]
    [InlineData(DeviceType.SmartPower, "pdu")]
    [InlineData(DeviceType.SmartPower, "strip")]
    [InlineData(DeviceType.SmartPower, "smart")]
    [InlineData(DeviceType.DeviceBridge, "bridge")]
    [InlineData(DeviceType.BuildingBridge, "bridge")]
    [InlineData(DeviceType.BuildingBridge, "ptp")]
    [InlineData(DeviceType.CellularModem, "lte")]
    [InlineData(DeviceType.CellularModem, "5g")]
    [InlineData(DeviceType.CellularModem, "modem")]
    [InlineData(DeviceType.CableModem, "modem")]
    [InlineData(DeviceType.CableModem, "docsis")]
    [InlineData(DeviceType.CloudKey, "cloud key")]
    [InlineData(DeviceType.CloudKey, "console")]
    [InlineData(DeviceType.NAS, "storage")]
    [InlineData(DeviceType.ProtectDevice, "camera")]
    [InlineData(DeviceType.ProtectDevice, "nvr")]
    [InlineData(DeviceType.TalkDevice, "phone")]
    [InlineData(DeviceType.AccessDevice, "door")]
    [InlineData(DeviceType.Accessory, "sfp")]
    [InlineData(DeviceType.TravelRouter, "router")]
    [InlineData(DeviceType.TravelRouter, "travel")]
    public void OtherTypes_MatchTheirSynonyms(DeviceType type, string query)
    {
        var d = Device(type, name: "Zz", ip: "");
        d.Mac = "";
        DeviceStatusFilter.Matches(d, null, query).Should().BeTrue();
    }

    [Fact]
    public void PresentTypes_LeadsWithGatewaySwitchAp_ThenByName_WithCounts()
    {
        var devices = new[]
        {
            Device(DeviceType.SmartPower), Device(DeviceType.AccessPoint), Device(DeviceType.AccessPoint),
            Device(DeviceType.CellularModem), Device(DeviceType.Switch), Device(DeviceType.Gateway),
        };

        DeviceStatusFilter.PresentTypes(devices).Should().Equal(
            (DeviceType.Gateway, 1), (DeviceType.Switch, 1), (DeviceType.AccessPoint, 2),
            (DeviceType.CellularModem, 1), (DeviceType.SmartPower, 1));
    }
}
