using FluentAssertions;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class WideChannelWidthRuleTests
{
    private readonly WideChannelWidthRule _rule = new();

    private static AccessPointSnapshot Ap(string mac, string name, RadioBand band, int width,
        bool widthOverride = false, bool meshChildOnBand = false, string? parentMac = null) => new()
    {
        Mac = mac,
        Name = name,
        Status = new(DeviceStatusKind.Online, "Online"),
        IsMeshChild = meshChildOnBand,
        MeshParentMac = parentMac,
        MeshUplinkBand = meshChildOnBand ? band : null,
        MeshUplinkChannel = meshChildOnBand ? 37 : null,
        Radios = new() { new RadioSnapshot { Band = band, Channel = 37, ChannelWidth = width, WidthIsOverride = widthOverride } }
    };

    private static WiFiOptimizerContext Context(params AccessPointSnapshot[] aps) => new()
    {
        AccessPoints = aps.ToList(), Clients = [], Wlans = [], Networks = [], LegacyClients = [], SteerableClients = []
    };

    [Fact]
    public void A_320_radio_is_an_info_issue_keyed_by_radio()
    {
        var issue = _rule.EvaluateAll(Context(Ap("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band6GHz, 320))).Single();

        issue.Severity.Should().Be(HealthIssueSeverity.Info);
        issue.Class.Should().Be(HealthIssueClass.Advisory);
        issue.Key.Should().Be("WIFI-WIDE-CHANNEL-WIDTH-001|aa:bb:cc:dd:ee:01/6e");
        issue.Description.Should().NotContain("deliberate");
    }

    [Fact]
    public void A_width_set_differently_from_the_bands_others_reads_as_deliberate()
    {
        var issue = _rule.EvaluateAll(Context(Ap("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band6GHz, 320, widthOverride: true))).Single();

        issue.Severity.Should().Be(HealthIssueSeverity.Info);
        issue.Description.Should().EndWith(RadioIntent.WidthHint(RadioBand.Band6GHz, meshBackhaul: false));
    }

    [Fact]
    public void A_mesh_backhaul_on_6GHz_is_still_skipped_outright()
    {
        var parent = Ap("aa:bb:cc:dd:ee:01", "AP-Parent", RadioBand.Band6GHz, 320);
        var child = Ap("aa:bb:cc:dd:ee:02", "AP-Child", RadioBand.Band6GHz, 320, meshChildOnBand: true, parentMac: parent.Mac);
        parent.MeshChildren.Add(new MeshChildInfo { Mac = child.Mac, Name = child.Name, UplinkBand = RadioBand.Band6GHz });

        _rule.EvaluateAll(Context(parent, child)).Should().BeEmpty();
    }
}
