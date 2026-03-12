using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.WiFi.Data;
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

    private static AccessPointSnapshot CreateAp(string mac, string name, RadioBand band, int channel, int txPower = 20) => new()
    {
        Mac = mac,
        Name = name,
        Radios = new()
        {
            new RadioSnapshot
            {
                Band = band,
                Channel = channel,
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
}
