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

    private static WiFiOptimizerContext Context(params AccessPointSnapshot[] aps) => Context(aps, []);

    private static WiFiOptimizerContext Context(AccessPointSnapshot[] aps, List<WirelessClientSnapshot> clients) => new()
    {
        AccessPoints = aps.ToList(), Clients = clients, Wlans = [], Networks = [], LegacyClients = [], SteerableClients = []
    };

    private static List<WirelessClientSnapshot> Clients(string apMac, RadioBand band, int count, int signal, int? negotiated) =>
        Enumerable.Range(1, count).Select(i => new WirelessClientSnapshot
        {
            Mac = $"cc:cc:cc:00:00:{i:D2}", ApMac = apMac, Band = band, Signal = signal, NegotiatedWidth = negotiated
        }).ToList();

    private const string SiteWide = "In UniFi Network: Settings > WiFi > Default WiFi Speeds > Channel Width - set 5 GHz to 80 MHz, then Save and Apply to All APs.";

    [Fact]
    public void Clients_that_all_negotiate_half_the_width_make_the_measured_issue()
    {
        var ap = Ap("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 160);
        ap.Radios[0].MeasuredMaxNegotiatedWidth = 80;

        var issue = _rule.EvaluateAll(Context([ap], Clients(ap.Mac, RadioBand.Band5GHz, 4, -55, 80))).Single();

        issue.Title.Should().Be("Unused Width on 5 GHz: AP-1");
        issue.Class.Should().Be(HealthIssueClass.Measured);
        issue.Description.Should().Be(
            "AP-1 is using 160 MHz on 5 GHz, and no client that can roam to it has negotiated more than 80 MHz in the last 7 days (4 on it now). " +
            "The extra width is not carrying traffic, and it makes the radio easier to interfere with.");
        issue.Recommendation.Should().Be(SiteWide);
    }

    [Fact]
    public void Without_the_weeks_history_a_snapshot_never_calls_the_width_unused()
    {
        var ap = Ap("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 160);

        _rule.EvaluateAll(Context([ap], Clients(ap.Mac, RadioBand.Band5GHz, 4, -55, 80))).Should().BeEmpty();
    }

    [Fact]
    public void A_client_that_negotiated_the_full_width_this_week_keeps_it_even_when_away()
    {
        var ap = Ap("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 160);
        ap.Radios[0].MeasuredMaxNegotiatedWidth = 160;

        _rule.EvaluateAll(Context([ap], Clients(ap.Mac, RadioBand.Band5GHz, 4, -55, 80))).Should().BeEmpty();
    }

    [Fact]
    public void One_client_using_the_full_width_is_enough_to_keep_it()
    {
        var ap = Ap("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 160);
        ap.Radios[0].MeasuredMaxNegotiatedWidth = 80;
        var clients = Clients(ap.Mac, RadioBand.Band5GHz, 4, -55, 80);
        clients[0].NegotiatedWidth = 160;

        _rule.EvaluateAll(Context([ap], clients)).Should().BeEmpty();
    }

    [Fact]
    public void One_console_client_and_the_radio_is_evaluated_as_today()
    {
        var ap = Ap("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 160);
        ap.Radios[0].MeasuredMaxNegotiatedWidth = 80;
        var clients = Clients(ap.Mac, RadioBand.Band5GHz, 4, -55, 80);
        clients[0].NegotiatedWidth = null;

        _rule.EvaluateAll(Context([ap], clients)).Should().BeEmpty();
    }

    [Fact]
    public void A_5GHz_backhaul_radio_with_weak_clients_is_not_asked_to_narrow()
    {
        var parent = Ap("aa:bb:cc:dd:ee:01", "AP-Parent", RadioBand.Band5GHz, 160);
        var child = Ap("aa:bb:cc:dd:ee:02", "AP-Child", RadioBand.Band5GHz, 160, meshChildOnBand: true, parentMac: parent.Mac);
        parent.MeshChildren.Add(new MeshChildInfo { Mac = child.Mac, Name = child.Name, UplinkBand = RadioBand.Band5GHz });

        _rule.EvaluateAll(Context([parent, child], Clients(child.Mac, RadioBand.Band5GHz, 4, -82, null))).Should().BeEmpty();
    }

    [Fact]
    public void On_a_band_with_a_backhaul_another_radios_issue_is_per_AP()
    {
        var parent = Ap("aa:bb:cc:dd:ee:01", "AP-Parent", RadioBand.Band5GHz, 160);
        var child = Ap("aa:bb:cc:dd:ee:02", "AP-Child", RadioBand.Band5GHz, 160, meshChildOnBand: true, parentMac: parent.Mac);
        parent.MeshChildren.Add(new MeshChildInfo { Mac = child.Mac, Name = child.Name, UplinkBand = RadioBand.Band5GHz });
        var other = Ap("aa:bb:cc:dd:ee:03", "AP-Other", RadioBand.Band5GHz, 160);

        var issue = _rule.EvaluateAll(Context([parent, child, other], Clients(other.Mac, RadioBand.Band5GHz, 4, -82, null))).Single();

        issue.Title.Should().Be("Wide Channel with Weak Clients on 5 GHz: AP-Other");
        issue.Recommendation.Should().Be(
            "In UniFi Network: Devices > AP-Other > Settings > Radios > 5 GHz > Channel Width - set it to 80 MHz on this AP only. " +
            "Do not use Apply to All APs here: AP-Parent and AP-Child carry a mesh backhaul on 5 GHz, and narrowing them would cut the link's capacity.");
    }

    [Fact]
    public void Without_a_backhaul_the_weak_signal_wording_is_unchanged()
    {
        var ap = Ap("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 160);

        var issue = _rule.EvaluateAll(Context([ap], Clients(ap.Mac, RadioBand.Band5GHz, 4, -82, null))).Single();

        issue.Recommendation.Should().Be(SiteWide);
    }

    [Fact]
    public void The_320_issue_notes_what_clients_negotiate_when_measured()
    {
        var ap = Ap("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band6GHz, 320);
        ap.Radios[0].MeasuredMaxNegotiatedWidth = 160;

        var issue = _rule.EvaluateAll(Context([ap], Clients(ap.Mac, RadioBand.Band6GHz, 4, -55, 160))).Single();

        issue.Severity.Should().Be(HealthIssueSeverity.Info);
        issue.Description.Should().EndWith(" No client that can roam to it has negotiated more than 160 MHz in the last 7 days.");
    }

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
