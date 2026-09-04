using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.WiFi.Data;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using NetworkOptimizer.WiFi.Services;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class CoChannelInterferenceRuleTests
{
    private readonly CoChannelInterferenceRule _rule;
    private readonly PropagationService _propagationService;

    public CoChannelInterferenceRuleTests()
    {
        var loader = new AntennaPatternLoader(NullLogger<AntennaPatternLoader>.Instance);
        _propagationService = new PropagationService(loader, NullLogger<PropagationService>.Instance);
        _rule = new CoChannelInterferenceRule(_propagationService);
    }

    private static AccessPointSnapshot CreateAp(
        string mac, string name, RadioBand band, int channel, int txPower = 20,
        int? width = null, int? center = null,
        string? meshParentMac = null) => new()
    {
        Mac = mac,
        Name = name,
        IsMeshChild = meshParentMac != null,
        MeshParentMac = meshParentMac,
        MeshUplinkBand = meshParentMac != null ? band : null,
        MeshUplinkChannel = meshParentMac != null ? channel : null,
        Radios = new()
        {
            new RadioSnapshot
            {
                Band = band,
                Channel = channel,
                ChannelWidth = width,
                CenterChannel = center,
                TxPower = txPower,
                AntennaGain = 3
            }
        }
    };

    private static WiFiOptimizerContext CreateContext(
        List<AccessPointSnapshot> aps,
        ApPropagationContext? propCtx = null) => new()
    {
        AccessPoints = aps,
        Clients = [],
        Wlans = [],
        Networks = [],
        LegacyClients = [],
        SteerableClients = [],
        PropagationContext = propCtx
    };

    [Fact]
    public void WithoutPropagationContext_AllCoChannelApsFlagged()
    {
        // Two APs on the same channel, no propagation context
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Kitchen", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-TinyHome", RadioBand.Band5GHz, 36)
        };

        var ctx = CreateContext(aps);
        var issues = _rule.EvaluateAll(ctx).ToList();

        issues.Should().HaveCount(1);
        issues[0].Title.Should().Contain("Co-Channel Interference");
        issues[0].Description.Should().Contain("AP-Kitchen");
        issues[0].Description.Should().Contain("AP-TinyHome");
    }

    [Fact]
    public void WithPropagationContext_FarApartAps_NoIssue()
    {
        // Two APs on the same channel, far apart (different buildings)
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Kitchen", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-TinyHome", RadioBand.Band5GHz, 36)
        };

        var propCtx = new ApPropagationContext
        {
            ApsByMac = new Dictionary<string, PropagationAp>
            {
                ["aa:bb:cc:dd:ee:01"] = new()
                {
                    Mac = "aa:bb:cc:dd:ee:01", Model = "U6-Pro",
                    Latitude = 36.0000, Longitude = -94.0000,
                    Floor = 1, TxPowerDbm = 20, AntennaGainDbi = 3, MountType = "ceiling"
                },
                ["aa:bb:cc:dd:ee:02"] = new()
                {
                    Mac = "aa:bb:cc:dd:ee:02", Model = "U6-Pro",
                    Latitude = 36.0018, Longitude = -94.0000, // ~200m away
                    Floor = 1, TxPowerDbm = 20, AntennaGainDbi = 3, MountType = "ceiling"
                }
            },
            WallsByFloor = new Dictionary<int, List<PropagationWall>>(),
            Buildings = null
        };

        var ctx = CreateContext(aps, propCtx);
        var issues = _rule.EvaluateAll(ctx).ToList();

        issues.Should().BeEmpty("APs are too far apart to interfere");
    }

    [Fact]
    public void WithPropagationContext_CloseAps_StillFlagged()
    {
        // Two APs on the same channel, close together
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Kitchen", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-LivingRoom", RadioBand.Band5GHz, 36)
        };

        var propCtx = new ApPropagationContext
        {
            ApsByMac = new Dictionary<string, PropagationAp>
            {
                ["aa:bb:cc:dd:ee:01"] = new()
                {
                    Mac = "aa:bb:cc:dd:ee:01", Model = "U6-Pro",
                    Latitude = 36.0000, Longitude = -94.0000,
                    Floor = 1, TxPowerDbm = 20, AntennaGainDbi = 3, MountType = "ceiling"
                },
                ["aa:bb:cc:dd:ee:02"] = new()
                {
                    Mac = "aa:bb:cc:dd:ee:02", Model = "U6-Pro",
                    Latitude = 36.000045, Longitude = -94.0000, // ~5m away
                    Floor = 1, TxPowerDbm = 20, AntennaGainDbi = 3, MountType = "ceiling"
                }
            },
            WallsByFloor = new Dictionary<int, List<PropagationWall>>(),
            Buildings = null
        };

        var ctx = CreateContext(aps, propCtx);
        var issues = _rule.EvaluateAll(ctx).ToList();

        issues.Should().HaveCount(1, "APs are close enough to interfere");
    }

    [Fact]
    public void MixedPlacement_TwoUnplacedAps_AssumedToInterfere()
    {
        // Four APs on same channel: two placed far apart, two not placed
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Kitchen", RadioBand.Band2_4GHz, 6),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-TinyHome", RadioBand.Band2_4GHz, 6),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-Unknown1", RadioBand.Band2_4GHz, 6),
            CreateAp("aa:bb:cc:dd:ee:04", "AP-Unknown2", RadioBand.Band2_4GHz, 6)
        };

        var propCtx = new ApPropagationContext
        {
            ApsByMac = new Dictionary<string, PropagationAp>
            {
                // Only two APs placed - others are unplaced (not in dictionary)
                ["aa:bb:cc:dd:ee:01"] = new()
                {
                    Mac = "aa:bb:cc:dd:ee:01", Model = "U6-Pro",
                    Latitude = 36.0000, Longitude = -94.0000,
                    Floor = 1, TxPowerDbm = 20, AntennaGainDbi = 3, MountType = "ceiling"
                },
                ["aa:bb:cc:dd:ee:02"] = new()
                {
                    Mac = "aa:bb:cc:dd:ee:02", Model = "U6-Pro",
                    Latitude = 36.0018, Longitude = -94.0000, // ~200m away
                    Floor = 1, TxPowerDbm = 20, AntennaGainDbi = 3, MountType = "ceiling"
                }
            },
            WallsByFloor = new Dictionary<int, List<PropagationWall>>(),
            Buildings = null
        };

        var ctx = CreateContext(aps, propCtx);
        var issues = _rule.EvaluateAll(ctx).ToList();

        // Two unplaced APs are assumed to interfere (kept by default).
        // The two placed APs are far apart and filtered out.
        // Result: 2 unplaced APs → co-channel warning fires.
        issues.Should().HaveCount(1);
        issues[0].Description.Should().Contain("AP-Unknown1");
        issues[0].Description.Should().Contain("AP-Unknown2");
        issues[0].Description.Should().NotContain("AP-Kitchen");
        issues[0].Description.Should().NotContain("AP-TinyHome");
    }

    [Fact]
    public void MixedPlacement_SingleUnplacedAp_NoIssue()
    {
        // Three APs on same channel: two placed far apart, one not placed
        // A single unplaced AP alone can't cause co-channel interference
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Kitchen", RadioBand.Band2_4GHz, 6),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-TinyHome", RadioBand.Band2_4GHz, 6),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-Unknown", RadioBand.Band2_4GHz, 6)
        };

        var propCtx = new ApPropagationContext
        {
            ApsByMac = new Dictionary<string, PropagationAp>
            {
                ["aa:bb:cc:dd:ee:01"] = new()
                {
                    Mac = "aa:bb:cc:dd:ee:01", Model = "U6-Pro",
                    Latitude = 36.0000, Longitude = -94.0000,
                    Floor = 1, TxPowerDbm = 20, AntennaGainDbi = 3, MountType = "ceiling"
                },
                ["aa:bb:cc:dd:ee:02"] = new()
                {
                    Mac = "aa:bb:cc:dd:ee:02", Model = "U6-Pro",
                    Latitude = 36.0018, Longitude = -94.0000, // ~200m away
                    Floor = 1, TxPowerDbm = 20, AntennaGainDbi = 3, MountType = "ceiling"
                }
            },
            WallsByFloor = new Dictionary<int, List<PropagationWall>>(),
            Buildings = null
        };

        var ctx = CreateContext(aps, propCtx);
        var issues = _rule.EvaluateAll(ctx).ToList();

        // Only 1 unplaced AP remains after filtering → can't have co-channel interference alone
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DifferentChannels_NoIssue()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 149)
        };

        var ctx = CreateContext(aps);
        var issues = _rule.EvaluateAll(ctx).ToList();

        issues.Should().BeEmpty();
    }

    [Fact]
    public void SingleApOnChannel_NoIssue()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36)
        };

        var ctx = CreateContext(aps);
        var issues = _rule.EvaluateAll(ctx).ToList();

        issues.Should().BeEmpty();
    }

    [Fact]
    public void SamePrimary_KeepsTheOriginalCopyAndChannel()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Kitchen", RadioBand.Band5GHz, 36, width: 80),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-TinyHome", RadioBand.Band5GHz, 36, width: 80)
        };

        var issues = _rule.EvaluateAll(CreateContext(aps)).ToList();

        issues.Should().HaveCount(1);
        issues[0].Title.Should().Be("Co-Channel Interference on 5 GHz Channel 36");
        issues[0].Description.Should().Be("2 APs (AP-Kitchen, AP-TinyHome) are using the same channel.");
        issues[0].AffectedChannels.Should().BeEquivalentTo(new[] { 36 });
    }

    // The measured main-site layout, 2026-09-01: 320 MHz radios on primaries 69 (block 33-93),
    // 5 (block 1-61, a mesh pair), and 165 (block 129-189). Kitchen shares 33-61 with the pair
    // and nothing with Tiny Home. Grouping by primary saw none of it.
    [Fact]
    public void OverlappingBlocks_FlagTheKitchenWithTheYardPair_NotWithTinyHome()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-Kitchen", RadioBand.Band6GHz, 69, width: 320, center: 63),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-FrontYard", RadioBand.Band6GHz, 5, width: 320, center: 31),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-BackYard", RadioBand.Band6GHz, 5, width: 320, center: 31,
                meshParentMac: "aa:bb:cc:dd:ee:02"),
            CreateAp("aa:bb:cc:dd:ee:04", "AP-TinyHome", RadioBand.Band6GHz, 165, width: 320, center: 159)
        };

        var issues = _rule.EvaluateAll(CreateContext(aps)).ToList();

        issues.Should().HaveCount(1);
        var issue = issues[0];
        issue.Title.Should().Be("Co-Channel Interference on 6 GHz Channels 5 and 69");
        issue.Description.Should().Contain("AP-Kitchen on channel 69 at 320 MHz (33-93)");
        issue.Description.Should().Contain("AP-FrontYard on channel 5 at 320 MHz (1-61)");
        issue.Description.Should().Contain("AP-BackYard on channel 5 at 320 MHz (1-61)");
        issue.Description.Should().Contain("They share channels 33-61.");
        issue.Description.Should().NotContain("AP-TinyHome");
        issue.AffectedChannels.Should().BeEquivalentTo(new[] { 5, 69 });
    }

    [Fact]
    public void MeasuredCenter_OverridesTheGuessedBlock()
    {
        // Guessed, 69 sits in 33-93 and overlaps 37's 1-61. Measured in 65-125 it does not.
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band6GHz, 69, width: 320, center: 95),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band6GHz, 37, width: 320, center: 31)
        };

        _rule.EvaluateAll(CreateContext(aps)).Should().BeEmpty();

        aps[0].Radios[0].CenterChannel = null;
        _rule.EvaluateAll(CreateContext(aps)).Should().HaveCount(1, "the guess puts 69 in 33-93");
    }

    [Fact]
    public void AnApWithTwoRadiosOnOneBand_IsListedOnce()
    {
        var twoRadio = CreateAp("aa:bb:cc:dd:ee:01", "AP-Dual", RadioBand.Band5GHz, 36, width: 80);
        twoRadio.Radios.Add(new RadioSnapshot { Band = RadioBand.Band5GHz, Channel = 36, ChannelWidth = 80, TxPower = 20 });
        var aps = new List<AccessPointSnapshot>
        {
            twoRadio,
            CreateAp("aa:bb:cc:dd:ee:02", "AP-Other", RadioBand.Band5GHz, 36, width: 80)
        };

        var issues = _rule.EvaluateAll(CreateContext(aps)).ToList();

        issues.Should().HaveCount(1);
        issues[0].Description.Should().Be("2 APs (AP-Dual, AP-Other) are using the same channel.");
    }

    [Fact]
    public void MeasuredAirtime_GradesTheOverlap()
    {
        AccessPointSnapshot Measured(string mac, string name, int channel, int util)
        {
            var ap = CreateAp(mac, name, RadioBand.Band5GHz, channel, width: 80);
            ap.Radios[0].MeasuredUtilization = util;
            return ap;
        }

        // Both quiet: an Info issue that says so.
        var quiet = _rule.EvaluateAll(CreateContext(new List<AccessPointSnapshot>
        {
            Measured("aa:bb:cc:dd:ee:01", "AP-1", 36, 3), Measured("aa:bb:cc:dd:ee:02", "AP-2", 36, 7)
        })).Single();
        quiet.Severity.Should().Be(HealthIssueSeverity.Info);
        quiet.ScoreImpact.Should().Be(-1);
        quiet.Description.Should().EndWith("Both radios are lightly used right now (3% to 7% of airtime busy), so this overlap costs little today.");

        // One busy: the warning, naming the airtime.
        var busy = _rule.EvaluateAll(CreateContext(new List<AccessPointSnapshot>
        {
            Measured("aa:bb:cc:dd:ee:01", "AP-1", 36, 44), Measured("aa:bb:cc:dd:ee:02", "AP-2", 36, 51)
        })).Single();
        busy.Severity.Should().Be(HealthIssueSeverity.Warning);
        busy.Description.Should().EndWith("The shared spectrum is busy: AP-1 at 44%, AP-2 at 51%. They are taking turns on the same air.");

        // One radio unmeasured: today's issue, ungraded.
        var mixed = _rule.EvaluateAll(CreateContext(new List<AccessPointSnapshot>
        {
            Measured("aa:bb:cc:dd:ee:01", "AP-1", 36, 3), CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 36, width: 80)
        })).Single();
        mixed.Severity.Should().Be(HealthIssueSeverity.Warning);
        mixed.Description.Should().Be("2 APs (AP-1, AP-2) are using the same channel.");
    }

    [Fact]
    public void FixedChannelsOnEveryRadio_AddTheHintWithoutSofteningTheWarning()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz, 36, width: 80),
            CreateAp("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz, 36, width: 80)
        };
        foreach (var ap in aps) ap.Radios[0].ChannelIsFixed = true;

        var issue = _rule.EvaluateAll(CreateContext(aps)).Single();

        issue.Severity.Should().Be(HealthIssueSeverity.Warning);
        issue.Description.Should().EndWith(RadioIntent.ChannelHint);
        issue.Key.Should().Be("WIFI-COCHANNEL-001|na|aa:bb:cc:dd:ee:01+aa:bb:cc:dd:ee:02");
    }

    [Fact]
    public void AMeshPairAloneOnOverlappingSpectrum_NoIssue()
    {
        var aps = new List<AccessPointSnapshot>
        {
            CreateAp("aa:bb:cc:dd:ee:02", "AP-FrontYard", RadioBand.Band6GHz, 5, width: 320, center: 31),
            CreateAp("aa:bb:cc:dd:ee:03", "AP-BackYard", RadioBand.Band6GHz, 5, width: 320, center: 31,
                meshParentMac: "aa:bb:cc:dd:ee:02")
        };

        _rule.EvaluateAll(CreateContext(aps)).Should().BeEmpty();
    }
}
