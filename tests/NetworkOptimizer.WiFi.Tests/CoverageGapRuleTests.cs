using FluentAssertions;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class CoverageGapRuleTests
{
    private readonly CoverageGapRule _rule = new();

    private static WiFiOptimizerContext CreateContext(
        List<AccessPointSnapshot> aps,
        List<WirelessClientSnapshot> clients) => new()
    {
        AccessPoints = aps,
        Clients = clients,
        Wlans = [],
        Networks = [],
        LegacyClients = [],
        SteerableClients = []
    };

    private static AccessPointSnapshot CreateAp(string mac, string name) => new()
    {
        Mac = mac,
        Name = name
    };

    private static WirelessClientSnapshot CreateClient(string apMac, int? signal, RadioBand band = RadioBand.Band5GHz) => new()
    {
        Mac = Guid.NewGuid().ToString("N")[..12],
        ApMac = apMac,
        Signal = signal,
        Band = band
    };

    [Fact]
    public void NoIssue_WhenNoAps()
    {
        var ctx = CreateContext([], []);
        _rule.Evaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void NoIssue_WhenFewerThanThreeClientsWithSignal()
    {
        var ap = CreateAp("aa:bb:cc:dd:ee:01", "Test AP");
        var clients = new List<WirelessClientSnapshot>
        {
            CreateClient(ap.Mac, -80),
            CreateClient(ap.Mac, -75)
        };

        var ctx = CreateContext([ap], clients);
        _rule.Evaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void NoIssue_WhenAllClientsHaveStrongSignal()
    {
        var ap = CreateAp("aa:bb:cc:dd:ee:01", "Test AP");
        var clients = new List<WirelessClientSnapshot>
        {
            CreateClient(ap.Mac, -50),
            CreateClient(ap.Mac, -55),
            CreateClient(ap.Mac, -60)
        };

        var ctx = CreateContext([ap], clients);
        _rule.Evaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void NoIssue_WhenWeakPercentageBelowThreshold()
    {
        // 1 of 3 = 33%, below 40%
        var ap = CreateAp("aa:bb:cc:dd:ee:01", "Test AP");
        var clients = new List<WirelessClientSnapshot>
        {
            CreateClient(ap.Mac, -82),
            CreateClient(ap.Mac, -50),
            CreateClient(ap.Mac, -55)
        };

        var ctx = CreateContext([ap], clients);
        _rule.Evaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void ReturnsIssue_WhenHalfOrMoreClientsHaveWeakSignal()
    {
        // 2 of 3 = 67%, above 40% (5 GHz weak threshold is -78)
        var ap = CreateAp("aa:bb:cc:dd:ee:01", "Test AP");
        var clients = new List<WirelessClientSnapshot>
        {
            CreateClient(ap.Mac, -82),
            CreateClient(ap.Mac, -85),
            CreateClient(ap.Mac, -50)
        };

        var ctx = CreateContext([ap], clients);
        var issue = _rule.Evaluate(ctx);

        issue.Should().NotBeNull();
        issue!.Title.Should().Contain("Test AP");
        issue.Description.Should().Contain("2 of 3");
        issue.Description.Should().Contain("67%");
    }

    [Fact]
    public void ClientsWithoutSignal_AreExcludedFromCountAndPercentage()
    {
        // 3 total clients, but only 2 have signal data.
        // Of those 2, 1 is weak = 50%.
        // But with only 2 signal clients, should NOT fire (below min threshold of 3).
        var ap = CreateAp("aa:bb:cc:dd:ee:01", "Test AP");
        var clients = new List<WirelessClientSnapshot>
        {
            CreateClient(ap.Mac, -82),
            CreateClient(ap.Mac, -50),
            CreateClient(ap.Mac, null)
        };

        var ctx = CreateContext([ap], clients);
        _rule.Evaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void ClientsWithoutSignal_DoNotInflateMinThreshold()
    {
        // 4 total clients, only 3 have signal. 2 of 3 weak = 67%.
        // Should fire because 3 clients with signal meets the threshold.
        var ap = CreateAp("aa:bb:cc:dd:ee:01", "Test AP");
        var clients = new List<WirelessClientSnapshot>
        {
            CreateClient(ap.Mac, -82),
            CreateClient(ap.Mac, -85),
            CreateClient(ap.Mac, -50),
            CreateClient(ap.Mac, null)
        };

        var ctx = CreateContext([ap], clients);
        var issue = _rule.Evaluate(ctx);

        issue.Should().NotBeNull();
        issue!.Description.Should().Contain("2 of 3");
        issue.Description.Should().Contain("67%");
    }

    [Fact]
    public void DisplayedCount_MatchesPercentageDenominator()
    {
        // Regression: percentage was calculated from clients with signal,
        // but display showed total client count, causing "50% (1 of 3)".
        var ap = CreateAp("aa:bb:cc:dd:ee:01", "Test AP");
        var clients = new List<WirelessClientSnapshot>
        {
            CreateClient(ap.Mac, -82),
            CreateClient(ap.Mac, -85),
            CreateClient(ap.Mac, -50)
        };

        var ctx = CreateContext([ap], clients);
        var issue = _rule.Evaluate(ctx);

        issue.Should().NotBeNull();
        // Verify the fraction in the description is mathematically consistent
        // "67% of clients (2 of 3)" - 2/3 = 67%
        issue!.Description.Should().Contain("2 of 3");
        issue.Description.Should().Contain("67%");
    }

    [Fact]
    public void MultipleAps_WithCoverageGaps_ReturnsMultiApIssue()
    {
        var ap1 = CreateAp("aa:bb:cc:dd:ee:01", "AP One");
        var ap2 = CreateAp("aa:bb:cc:dd:ee:02", "AP Two");
        var clients = new List<WirelessClientSnapshot>
        {
            CreateClient(ap1.Mac, -82),
            CreateClient(ap1.Mac, -85),
            CreateClient(ap1.Mac, -50),
            CreateClient(ap2.Mac, -82),
            CreateClient(ap2.Mac, -85),
            CreateClient(ap2.Mac, -90)
        };

        var ctx = CreateContext([ap1, ap2], clients);
        var issue = _rule.Evaluate(ctx);

        issue.Should().NotBeNull();
        issue!.Title.Should().Contain("2 APs");
        issue.AffectedEntity.Should().Contain("AP One");
        issue.AffectedEntity.Should().Contain("AP Two");
    }

    [Fact]
    public void MixedBands_SameSignal_DifferentOutcome()
    {
        // -75 dBm is weak on 2.4 GHz but fair on 5 GHz.
        // AP with 2.4 GHz clients should trigger, AP with 5 GHz clients should not.
        var ap24 = CreateAp("aa:bb:cc:dd:ee:01", "AP 2.4G");
        var ap5 = CreateAp("aa:bb:cc:dd:ee:02", "AP 5G");
        var clients = new List<WirelessClientSnapshot>
        {
            // 2.4 GHz: all at -75 dBm = all weak (threshold -67)
            CreateClient(ap24.Mac, -75, RadioBand.Band2_4GHz),
            CreateClient(ap24.Mac, -75, RadioBand.Band2_4GHz),
            CreateClient(ap24.Mac, -75, RadioBand.Band2_4GHz),
            // 5 GHz: all at -75 dBm = all fair (threshold -78), NOT weak
            CreateClient(ap5.Mac, -75, RadioBand.Band5GHz),
            CreateClient(ap5.Mac, -75, RadioBand.Band5GHz),
            CreateClient(ap5.Mac, -75, RadioBand.Band5GHz),
        };

        var ctx = CreateContext([ap24, ap5], clients);
        var issue = _rule.Evaluate(ctx);

        issue.Should().NotBeNull();
        // Only the 2.4 GHz AP should be flagged
        issue!.Title.Should().Contain("AP 2.4G");
        issue.Title.Should().NotContain("AP 5G");
    }

    [Fact]
    public void Band6GHz_WeakAt88_NotWeakAt85()
    {
        // 6 GHz weak threshold is -87. -85 dBm is fair, -88 dBm is weak.
        var ap = CreateAp("aa:bb:cc:dd:ee:01", "AP 6G");
        var clients = new List<WirelessClientSnapshot>
        {
            CreateClient(ap.Mac, -88, RadioBand.Band6GHz),
            CreateClient(ap.Mac, -90, RadioBand.Band6GHz),
            CreateClient(ap.Mac, -50, RadioBand.Band6GHz),
        };

        var ctx = CreateContext([ap], clients);
        var issue = _rule.Evaluate(ctx);
        issue.Should().NotBeNull("two of three 6 GHz clients are below -87 dBm");

        // Now same AP but at -85 dBm (fair, not weak)
        var clientsFair = new List<WirelessClientSnapshot>
        {
            CreateClient(ap.Mac, -85, RadioBand.Band6GHz),
            CreateClient(ap.Mac, -85, RadioBand.Band6GHz),
            CreateClient(ap.Mac, -50, RadioBand.Band6GHz),
        };

        var ctxFair = CreateContext([ap], clientsFair);
        _rule.Evaluate(ctxFair).Should().BeNull("-85 dBm on 6 GHz is fair, not weak");
    }

    private static AccessPointSnapshot Ap(string name, string model = "U7 Pro", bool isGateway = false, string? txMode = "medium") => new()
    {
        Mac = "aa:bb:cc:dd:ee:01", Name = name, Model = model, IsGateway = isGateway,
        Radios = [new RadioSnapshot { Band = RadioBand.Band5GHz, Channel = 36, TxPowerMode = txMode }]
    };

    [Fact]
    public void Single_ap_offers_tx_power_when_it_can_go_up()
    {
        CoverageGapRule.SingleApRecommendation(Ap("AP"), [RadioBand.Band5GHz]).Should().Be(
            "Consider: (1) increasing TX power on this AP, or (2) adding an AP closer to weak clients.");
    }

    [Theory]
    [InlineData("high")]
    [InlineData("auto")] // Auto is High in UniFi Network today
    public void Single_ap_at_full_power_drops_the_tx_power_step(string mode)
    {
        CoverageGapRule.SingleApRecommendation(Ap("AP", txMode: mode), [RadioBand.Band5GHz]).Should().Be(
            "Consider adding an AP closer to weak clients.");
    }

    [Fact]
    public void Custom_power_at_the_radio_max_counts_as_full()
    {
        var ap = Ap("AP", txMode: "custom");
        ap.Radios[0].TxPower = 23;
        ap.Radios[0].MaxTxPower = 23;
        CoverageGapRule.SingleApRecommendation(ap, [RadioBand.Band5GHz]).Should().Be(
            "Consider adding an AP closer to weak clients.");
    }

    [Fact]
    public void An_express_in_ap_mode_offers_moving_it()
    {
        CoverageGapRule.SingleApRecommendation(Ap("Den", model: "UX7"), [RadioBand.Band5GHz]).Should().Be(
            "Consider: (1) moving this AP closer to weak clients, (2) increasing TX power on this AP, or (3) adding an AP closer to weak clients.");
    }

    [Theory]
    [InlineData("UX", true)]   // an Express running as the gateway sits where the WAN cable is
    [InlineData("UDR7", false)] // a Dream Router is always the gateway
    [InlineData("U7 Pro", false)]
    public void Only_an_express_in_ap_mode_is_movable(string model, bool isGateway)
    {
        CoverageGapRule.SingleApRecommendation(Ap("AP", model: model, isGateway: isGateway), [RadioBand.Band5GHz])
            .Should().NotContain("moving this AP");
    }

    [Fact]
    public void Several_aps_name_who_each_step_applies_to()
    {
        var den = Ap("Den", model: "UX");
        var hall = Ap("Hall", txMode: "high");
        var porch = Ap("Porch");
        CoverageGapRule.MultiApRecommendation([(den, [RadioBand.Band5GHz]), (hall, [RadioBand.Band5GHz]), (porch, [RadioBand.Band5GHz])])
            .Should().Be("Consider: (1) moving Den closer to weak clients, (2) increasing TX power on Den and Porch, or (3) adding APs in areas with weak coverage.");
    }
}
