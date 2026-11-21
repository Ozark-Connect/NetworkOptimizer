using FluentAssertions;
using NetworkOptimizer.WiFi.Analyzers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

/// <summary>
/// Fast roaming (802.11r) is suggested only for an enabled SSID that has it off, never blindly: a site may keep it off
/// on purpose on an IoT SSID.
/// </summary>
public class SiteHealthScorerFastRoamingTests
{
    private const string CoverageOnly = "Check for coverage gaps between these APs.";

    #region SSIDs with fast roaming off

    [Fact]
    public void OffSsids_ListsEnabledSsidsWithItOff()
    {
        var off = SiteHealthScorer.FastRoamingOffSsids([Wlan("Home", fastRoaming: true), Wlan("IoT", fastRoaming: false)]);

        off.Should().Equal("IoT");
    }

    [Fact]
    public void OffSsids_IgnoresDisabledSsids()
    {
        SiteHealthScorer.FastRoamingOffSsids([Wlan("Home", fastRoaming: true), Wlan("Old", fastRoaming: false, enabled: false)])
            .Should().BeEmpty();
    }

    [Fact]
    public void OffSsids_UnknownWlans_Empty()
    {
        SiteHealthScorer.FastRoamingOffSsids(null).Should().BeEmpty();
    }

    [Fact]
    public void OffSsids_LeavesOutIotSsids()
    {
        // An IoT SSID keeps fast roaming off on purpose
        var off = SiteHealthScorer.FastRoamingOffSsids(
            [Wlan("Home", fastRoaming: false), Wlan("IoT", fastRoaming: false)], new HashSet<string> { "wlan-IoT" });

        off.Should().Equal("Home");
    }

    [Fact]
    public void Calculate_RoamFailures_OnlyIotSsidOff_NoFastRoamingAdvice()
    {
        var issue = new SiteHealthScorer().Calculate([], [], FailingEdge(),
                [Wlan("Home", fastRoaming: true), Wlan("IoT", fastRoaming: false)], new HashSet<string> { "wlan-IoT" })
            .Issues.Should().ContainSingle(i => i.RuleId == "WIFI-ROAM-FAILURES-001").Subject;

        issue.Recommendation.Should().Be(CoverageOnly);
    }

    [Fact]
    public void IoTSsidsOf_DirectAndPpskBindings()
    {
        var iot = new NetworkOptimizer.Audit.Models.NetworkInfo
        {
            Id = "net-iot", Name = "IoT", VlanId = 30, Enabled = true, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.IoT
        };
        var home = new NetworkOptimizer.Audit.Models.NetworkInfo
        {
            Id = "net-home", Name = "Home", VlanId = 10, Enabled = true, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.Home
        };
        var direct = Wlan("Things", fastRoaming: false);
        direct.NetworkId = "net-iot";
        var ppsk = Wlan("Shared", fastRoaming: false);
        ppsk.NetworkId = "net-home";
        ppsk.PpskNetworkIds = ["net-iot"];
        var main = Wlan("Main", fastRoaming: true);
        main.NetworkId = "net-home";

        NetworkOptimizer.WiFi.Rules.WiFiOptimizerContext.IoTSsidsOf([direct, ppsk, main], [iot, home])
            .Select(w => w.Name).Should().BeEquivalentTo(["Things", "Shared"]);
    }

    #endregion

    #region Roaming failures recommendation

    [Fact]
    public void Recommendation_EverySsidOn_CoverageOnly()
    {
        SiteHealthScorer.RoamFailureRecommendation([]).Should().Be(CoverageOnly);
    }

    [Fact]
    public void Recommendation_SomeSsidsOff_NamesThemWithTheIotException()
    {
        SiteHealthScorer.RoamFailureRecommendation(["IoT", "Guest"]).Should().Be(
            "Check for coverage gaps between these APs. Fast roaming is off on IoT, Guest; turning it on can help, except where IoT or older devices need it off.");
    }

    [Fact]
    public void Calculate_RoamFailures_EverySsidOn_NoFastRoamingAdvice()
    {
        var issue = RoamFailures([Wlan("Home", fastRoaming: true)]);

        issue.Recommendation.Should().Be(CoverageOnly);
    }

    [Fact]
    public void Calculate_RoamFailures_IotSsidOff_NamesIt()
    {
        var issue = RoamFailures([Wlan("Home", fastRoaming: true), Wlan("IoT", fastRoaming: false)]);

        issue.Recommendation.Should().Contain("Fast roaming is off on IoT;");
    }

    [Fact]
    public void Calculate_RoamFailures_NoWlanData_NoFastRoamingAdvice()
    {
        RoamFailures(null).Recommendation.Should().Be(CoverageOnly);
    }

    #endregion

    #region Roaming Performance factor

    [Fact]
    public void Factor_LowFastRoamUse_EverySsidOn_NoSuggestion()
    {
        // Every SSID offers it, so low use means the clients lack 802.11r
        FastRoamingFactor([Wlan("Home", fastRoaming: true)]).Description.Should().BeNull();
    }

    [Fact]
    public void Factor_LowFastRoamUse_SomeSsidOff_Suggests()
    {
        FastRoamingFactor([Wlan("Home", fastRoaming: false)]).Description.Should().Be("Consider enabling fast roaming");
    }

    [Fact]
    public void Factor_LowFastRoamUse_NoWlanData_NoSuggestion()
    {
        FastRoamingFactor(null).Description.Should().BeNull();
    }

    #endregion

    #region Helpers

    private static HealthIssue RoamFailures(List<WlanConfiguration>? wlans) =>
        new SiteHealthScorer().Calculate([], [], FailingEdge(), wlans)
            .Issues.Should().ContainSingle(i => i.RuleId == "WIFI-ROAM-FAILURES-001").Subject;

    private static ScoreFactor FastRoamingFactor(List<WlanConfiguration>? wlans) =>
        new SiteHealthScorer().Calculate([], [], FailingEdge(), wlans)
            .RoamingPerformance.Factors.Should().ContainSingle(f => f.Name == "Fast roaming (802.11r) usage").Subject;

    /// <summary>One AP pair at 80% roam success with no fast roams: below the healthy rate, enough attempts to count.</summary>
    private static RoamingTopology FailingEdge() => new()
    {
        Vertices =
        [
            new RoamingVertex { Mac = "aa:bb:cc:00:00:01", Name = "AP-1" },
            new RoamingVertex { Mac = "aa:bb:cc:00:00:02", Name = "AP-2" }
        ],
        Edges =
        [
            new RoamingEdge
            {
                Endpoint1Mac = "aa:bb:cc:00:00:01",
                Endpoint2Mac = "aa:bb:cc:00:00:02",
                TotalRoamAttempts = 10,
                TotalSuccessfulRoams = 8,
                Endpoint1ToEndpoint2 = new RoamingDirectionStats { RoamAttempts = 5, SuccessfulRoams = 4 },
                Endpoint2ToEndpoint1 = new RoamingDirectionStats { RoamAttempts = 5, SuccessfulRoams = 4 }
            }
        ]
    };

    private static WlanConfiguration Wlan(string name, bool fastRoaming, bool enabled = true) =>
        new() { Id = $"wlan-{name}", Name = name, Enabled = enabled, FastRoamingEnabled = fastRoaming };

    #endregion
}
