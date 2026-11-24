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
