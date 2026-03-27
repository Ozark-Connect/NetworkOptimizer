using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.WiFi.Data;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using NetworkOptimizer.WiFi.Services;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class LoadImbalanceRuleTests
{
    private readonly LoadImbalanceRule _rule;

    public LoadImbalanceRuleTests()
    {
        var loader = new AntennaPatternLoader(NullLogger<AntennaPatternLoader>.Instance);
        var propagationService = new PropagationService(loader, NullLogger<PropagationService>.Instance);
        _rule = new LoadImbalanceRule(propagationService);
    }

    private static AccessPointSnapshot CreateAp(
        string mac, string name, int totalClients,
        RadioBand band = RadioBand.Band5GHz, int channel = 36) => new()
    {
        Mac = mac,
        Name = name,
        TotalClients = totalClients,
        Radios = new()
        {
            new RadioSnapshot { Band = band, Channel = channel, TxPower = 20, AntennaGain = 3 }
        }
    };

    private static WirelessClientSnapshot CreateClient(string apMac, int? signal = -50) => new()
    {
        Mac = $"cc:cc:cc:{Guid.NewGuid().ToString()[..8]}",
        ApMac = apMac,
        Signal = signal
    };

    private static WiFiOptimizerContext CreateContext(
        List<AccessPointSnapshot> aps,
        List<WirelessClientSnapshot>? clients = null,
        ApPropagationContext? propCtx = null) => new()
    {
        AccessPoints = aps,
        Clients = clients ?? [],
        Wlans = [],
        Networks = [],
        LegacyClients = [],
        SteerableClients = [],
        PropagationContext = propCtx
    };

    // ---------------------------------------------------------------
    // Basic threshold behavior
    // ---------------------------------------------------------------

    [Fact]
    public void SingleAp_NoIssue()
    {
        var aps = new List<AccessPointSnapshot> { CreateAp("aa:bb:cc:dd:ee:01", "AP-1", 10) };
        var ctx = CreateContext(aps);

        _rule.Evaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void NoClients_NoIssue()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", 0),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", 0)
        };
        var ctx = CreateContext(aps);

        _rule.Evaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void BalancedLoad_NoIssue()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", 10),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", 10)
        };
        var clients = Enumerable.Range(0, 20).Select(_ => CreateClient("aa:bb:cc:dd:ee:01")).ToList();
        var ctx = CreateContext(aps, clients);

        _rule.Evaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void HighImbalance_Warning()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Busy", 18),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Idle", 2)
        };
        var clients = Enumerable.Range(0, 20).Select(_ => CreateClient("aa:bb:cc:dd:ee:01")).ToList();
        var ctx = CreateContext(aps, clients);

        var issue = _rule.Evaluate(ctx);

        issue.Should().NotBeNull();
        issue!.Severity.Should().Be(HealthIssueSeverity.Warning);
        issue.Title.Should().Be("Significant Load Imbalance");
        issue.Description.Should().Contain("AP-Busy").And.Contain("AP-Idle");
        issue.ScoreImpact.Should().Be(-8);
    }

    [Fact]
    public void NoPropagationContext_RecommendationSuggestsSignalMap()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Busy", 18),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Idle", 2)
        };
        var clients = Enumerable.Range(0, 20).Select(_ => CreateClient("aa:bb:cc:dd:ee:01")).ToList();
        var ctx = CreateContext(aps, clients);

        var issue = _rule.Evaluate(ctx);

        issue.Should().NotBeNull();
        issue!.Recommendation.Should().Contain("Signal Map");
    }

    [Fact]
    public void WithPropagationContext_RecommendationOmitsSignalMap()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Busy", 18),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Idle", 2)
        };
        var clients = Enumerable.Range(0, 20).Select(_ => CreateClient("aa:bb:cc:dd:ee:01")).ToList();

        // Close together - should still warn
        var propCtx = CreatePropagationContext(
            "aa:bb:cc:dd:ee:01", 36.0000, -94.0000,
            "aa:bb:cc:dd:ee:02", 36.000045, -94.0000); // ~5m apart

        var ctx = CreateContext(aps, clients, propCtx);
        var issue = _rule.Evaluate(ctx);

        issue.Should().NotBeNull();
        issue!.Recommendation.Should().NotContain("Signal Map");
    }

    // ---------------------------------------------------------------
    // Tie-breaking: maxAp and minAp must be different APs
    // ---------------------------------------------------------------

    [Fact]
    public void TiedClientCounts_DifferentAps_InDescription()
    {
        // 3 APs: two with 8 clients, one with 0 → CV > 50%, rule fires
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "U7 Lite", 8),
            CreateAp("aa:bb:cc:dd:ee:02", "U7 Pro", 8),
            CreateAp("aa:bb:cc:dd:ee:03", "Phantom", 0)
        };
        var clients = Enumerable.Range(0, 16).Select(_ => CreateClient("aa:bb:cc:dd:ee:01")).ToList();
        var ctx = CreateContext(aps, clients);

        var issue = _rule.Evaluate(ctx);

        issue.Should().NotBeNull();
        // maxAp and minAp must be different APs
        issue!.AffectedEntity.Should().Contain("Phantom");
        // The description must mention two DIFFERENT AP names
        var parts = issue.Description.Split(" while ");
        parts.Should().HaveCount(2);
        parts[0].Should().NotBe(parts[1]);
    }

    [Fact]
    public void TwoAps_EqualClients_NoIssue()
    {
        // 2 APs with identical client count → CV = 0 → below threshold
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", 8),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", 8)
        };
        var clients = Enumerable.Range(0, 16).Select(_ => CreateClient("aa:bb:cc:dd:ee:01")).ToList();
        var ctx = CreateContext(aps, clients);

        _rule.Evaluate(ctx).Should().BeNull("CV is 0% when both APs have equal clients");
    }

    // ---------------------------------------------------------------
    // RF distance: suppress or downgrade when APs are far apart
    // ---------------------------------------------------------------

    [Fact]
    public void FarApart_AllStrongSignal_Suppressed()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-House", 15),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Garage", 2)
        };

        // All clients on the busy AP have strong signal
        var clients = Enumerable.Range(0, 17).Select(i =>
            CreateClient("aa:bb:cc:dd:ee:01", signal: -45)).ToList();

        // APs ~200m apart
        var propCtx = CreatePropagationContext(
            "aa:bb:cc:dd:ee:01", 36.0000, -94.0000,
            "aa:bb:cc:dd:ee:02", 36.0018, -94.0000);

        var ctx = CreateContext(aps, clients, propCtx);

        _rule.Evaluate(ctx).Should().BeNull("APs are in separate zones with all strong-signal clients");
    }

    [Fact]
    public void FarApart_SomeWeakSignal_DowngradedToInfo()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-House", 15),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Garage", 2)
        };

        // Mix of strong and weak signal clients on the busy AP
        var clients = new List<WirelessClientSnapshot>
        {
            CreateClient("aa:bb:cc:dd:ee:01", signal: -45),
            CreateClient("aa:bb:cc:dd:ee:01", signal: -45),
            CreateClient("aa:bb:cc:dd:ee:01", signal: -75), // weak
        };
        // Pad to match TotalClients
        for (int i = 0; i < 14; i++)
            clients.Add(CreateClient("aa:bb:cc:dd:ee:01", signal: -50));

        var propCtx = CreatePropagationContext(
            "aa:bb:cc:dd:ee:01", 36.0000, -94.0000,
            "aa:bb:cc:dd:ee:02", 36.0018, -94.0000);

        var ctx = CreateContext(aps, clients, propCtx);
        var issue = _rule.Evaluate(ctx);

        issue.Should().NotBeNull();
        issue!.Severity.Should().Be(HealthIssueSeverity.Info);
        issue.Description.Should().Contain("separate coverage zones");
        issue.ScoreImpact.Should().Be(-2);
    }

    [Fact]
    public void CloseAps_StillWarning()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Kitchen", 18),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Living", 2)
        };
        var clients = Enumerable.Range(0, 20).Select(_ => CreateClient("aa:bb:cc:dd:ee:01")).ToList();

        // ~5m apart - well within interference range
        var propCtx = CreatePropagationContext(
            "aa:bb:cc:dd:ee:01", 36.0000, -94.0000,
            "aa:bb:cc:dd:ee:02", 36.000045, -94.0000);

        var ctx = CreateContext(aps, clients, propCtx);
        var issue = _rule.Evaluate(ctx);

        issue.Should().NotBeNull();
        issue!.Severity.Should().Be(HealthIssueSeverity.Warning);
        issue.ScoreImpact.Should().Be(-8);
    }

    [Fact]
    public void FarApart_ClientWithNullSignal_NotSuppressed()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-House", 15),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Garage", 2)
        };

        // One client missing signal data
        var clients = new List<WirelessClientSnapshot>
        {
            CreateClient("aa:bb:cc:dd:ee:01", signal: -45),
            CreateClient("aa:bb:cc:dd:ee:01", signal: null), // missing signal
        };
        for (int i = 0; i < 15; i++)
            clients.Add(CreateClient("aa:bb:cc:dd:ee:01", signal: -45));

        var propCtx = CreatePropagationContext(
            "aa:bb:cc:dd:ee:01", 36.0000, -94.0000,
            "aa:bb:cc:dd:ee:02", 36.0018, -94.0000);

        var ctx = CreateContext(aps, clients, propCtx);
        var issue = _rule.Evaluate(ctx);

        // Should NOT suppress entirely because one client has null signal
        issue.Should().NotBeNull();
        issue!.Severity.Should().Be(HealthIssueSeverity.Info, "null signal prevents full suppression");
    }

    [Fact]
    public void PropagationContext_OneApNotPlaced_FallsThrough()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Kitchen", 18),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Living", 2)
        };
        var clients = Enumerable.Range(0, 20).Select(_ => CreateClient("aa:bb:cc:dd:ee:01")).ToList();

        // Only one AP is placed on the map
        var propCtx = new ApPropagationContext
        {
            ApsByMac = new Dictionary<string, PropagationAp>
            {
                ["aa:bb:cc:dd:ee:01"] = new()
                {
                    Mac = "aa:bb:cc:dd:ee:01", Model = "U6-Pro",
                    Latitude = 36.0, Longitude = -94.0,
                    Floor = 1, TxPowerDbm = 20, AntennaGainDbi = 3, MountType = "ceiling"
                }
            },
            WallsByFloor = new Dictionary<int, List<PropagationWall>>(),
            Buildings = null
        };

        var ctx = CreateContext(aps, clients, propCtx);
        var issue = _rule.Evaluate(ctx);

        // Falls through to normal Warning path (can't check RF distance with only 1 placed AP)
        issue.Should().NotBeNull();
        issue!.Severity.Should().Be(HealthIssueSeverity.Warning);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static ApPropagationContext CreatePropagationContext(
        string mac1, double lat1, double lng1,
        string mac2, double lat2, double lng2) => new()
    {
        ApsByMac = new Dictionary<string, PropagationAp>
        {
            [mac1] = new()
            {
                Mac = mac1, Model = "U6-Pro",
                Latitude = lat1, Longitude = lng1,
                Floor = 1, TxPowerDbm = 20, AntennaGainDbi = 3, MountType = "ceiling"
            },
            [mac2] = new()
            {
                Mac = mac2, Model = "U6-Pro",
                Latitude = lat2, Longitude = lng2,
                Floor = 1, TxPowerDbm = 20, AntennaGainDbi = 3, MountType = "ceiling"
            }
        },
        WallsByFloor = new Dictionary<int, List<PropagationWall>>(),
        Buildings = null
    };
}
