using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class FirmwareAlignTests
{
    private static ConfigState Devices(params (string Mac, string Version)[] devices)
    {
        var s = new ConfigState();
        s.Collections["device"] = devices
            .Select(d => (JsonObject)new JsonObject { ["mac"] = d.Mac, ["name"] = "Switch " + d.Mac[^2..], ["model"] = "USL16P", ["version"] = d.Version })
            .ToList();
        return s;
    }

    [Fact]
    public void OnlyAdoptedDevicesBehindTheSnapshotAreListed()
    {
        var snapshot = Devices(("aa:bb:cc:dd:ee:01", "7.2.123.1"), ("aa:bb:cc:dd:ee:02", "7.2.123.1"), ("aa:bb:cc:dd:ee:03", "7.2.123.1"));
        var live = Devices(("AA:BB:CC:DD:EE:01", "7.1.66.1"), ("aa:bb:cc:dd:ee:02", "7.3.10.1"));

        var rows = ChangeControlService.FirmwareBehind(snapshot, live);

        rows.Should().ContainSingle();
        rows[0].Mac.Should().Be("aa:bb:cc:dd:ee:01");
        rows[0].Current.Should().Be("7.1.66.1");
        rows[0].Target.Should().Be("7.2.123.1");
    }
}
