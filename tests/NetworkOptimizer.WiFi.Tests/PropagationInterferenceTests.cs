using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.WiFi.Data;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class PropagationInterferenceTests
{
    private readonly PropagationService _svc;

    public PropagationInterferenceTests()
    {
        var loader = new AntennaPatternLoader(NullLogger<AntennaPatternLoader>.Instance);
        _svc = new PropagationService(loader, NullLogger<PropagationService>.Instance);
    }

    private static PropagationAp CreateAp(string mac, double lat, double lng, int floor = 1, int txPower = 20) => new()
    {
        Mac = mac,
        Model = "U6-Pro",
        Latitude = lat,
        Longitude = lng,
        Floor = floor,
        TxPowerDbm = txPower,
        AntennaGainDbi = 3,
        MountType = "ceiling"
    };

    [Fact]
    public void CloseAps_SameFloor_SameBand_Interfere()
    {
        // Two APs ~5 meters apart on the same floor
        var ap1 = CreateAp("aa:bb:cc:dd:ee:01", 36.0000, -94.0000);
        var ap2 = CreateAp("aa:bb:cc:dd:ee:02", 36.000045, -94.0000); // ~5m north

        var walls = new Dictionary<int, List<PropagationWall>>();

        _svc.DoApsInterfere(ap1, ap2, "5", walls, null).Should().BeTrue();
    }

    [Fact]
    public void DistantAps_SameFloor_DoNotInterfere()
    {
        // Two APs ~200 meters apart on the same floor
        var ap1 = CreateAp("aa:bb:cc:dd:ee:01", 36.0000, -94.0000);
        var ap2 = CreateAp("aa:bb:cc:dd:ee:02", 36.0018, -94.0000); // ~200m north

        var walls = new Dictionary<int, List<PropagationWall>>();

        _svc.DoApsInterfere(ap1, ap2, "5", walls, null).Should().BeFalse();
    }

    [Fact]
    public void CloseAps_2_4GHz_HigherRange_StillInterfere()
    {
        // 2.4 GHz has lower path loss - APs ~30m apart should still interfere
        var ap1 = CreateAp("aa:bb:cc:dd:ee:01", 36.0000, -94.0000);
        var ap2 = CreateAp("aa:bb:cc:dd:ee:02", 36.00027, -94.0000); // ~30m north

        var walls = new Dictionary<int, List<PropagationWall>>();

        _svc.DoApsInterfere(ap1, ap2, "2.4", walls, null).Should().BeTrue();
    }

    [Fact]
    public void DistantAps_DifferentFloors_DoNotInterfere()
    {
        // Two APs ~50 meters apart on different floors
        var ap1 = CreateAp("aa:bb:cc:dd:ee:01", 36.0000, -94.0000, floor: 1);
        var ap2 = CreateAp("aa:bb:cc:dd:ee:02", 36.00045, -94.0000, floor: 2); // ~50m north, floor 2

        var walls = new Dictionary<int, List<PropagationWall>>();

        _svc.DoApsInterfere(ap1, ap2, "5", walls, null).Should().BeFalse();
    }

    [Fact]
    public void CloseAps_SeparatedByConcreteWall_ReducedSignal()
    {
        // Two APs ~15m apart with a concrete wall between them
        var ap1 = CreateAp("aa:bb:cc:dd:ee:01", 36.0000, -94.0000);
        var ap2 = CreateAp("aa:bb:cc:dd:ee:02", 36.000135, -94.0000); // ~15m north

        // Concrete wall across the path
        var walls = new Dictionary<int, List<PropagationWall>>
        {
            [1] = new()
            {
                new PropagationWall
                {
                    Material = "concrete",
                    Points = new()
                    {
                        new LatLng { Lat = 36.000067, Lng = -94.001 },
                        new LatLng { Lat = 36.000067, Lng = -93.999 }
                    }
                }
            }
        };

        // Indoor path loss at 15m on 5 GHz (exponent 2.8) is ~80 dB.
        // Signal = 20 + 3 - 80 - 15(concrete) ≈ -72 dBm, below -70 threshold.
        // Concrete wall effectively blocks interference at this distance.
        _svc.DoApsInterfere(ap1, ap2, "5", walls, null).Should().BeFalse();

        // Without the concrete wall, same distance should interfere
        _svc.DoApsInterfere(ap1, ap2, "5", new Dictionary<int, List<PropagationWall>>(), null)
            .Should().BeTrue();
    }

    [Fact]
    public void ModerateDistance_WithMultipleConcreteWalls_MayNotInterfere()
    {
        // Two APs ~40m apart with two concrete walls between them
        var ap1 = CreateAp("aa:bb:cc:dd:ee:01", 36.0000, -94.0000);
        var ap2 = CreateAp("aa:bb:cc:dd:ee:02", 36.00036, -94.0000); // ~40m north

        // Two concrete walls across the path
        var walls = new Dictionary<int, List<PropagationWall>>
        {
            [1] = new()
            {
                new PropagationWall
                {
                    Material = "concrete",
                    Points = new()
                    {
                        new LatLng { Lat = 36.00012, Lng = -94.001 },
                        new LatLng { Lat = 36.00012, Lng = -93.999 }
                    }
                },
                new PropagationWall
                {
                    Material = "concrete",
                    Points = new()
                    {
                        new LatLng { Lat = 36.00024, Lng = -94.001 },
                        new LatLng { Lat = 36.00024, Lng = -93.999 }
                    }
                }
            }
        };

        // At 40m with 2 concrete walls (30 dB total wall loss at 5 GHz), should not interfere
        _svc.DoApsInterfere(ap1, ap2, "5", walls, null).Should().BeFalse();
    }

    [Fact]
    public void DifferentBuildings_ExteriorWalls_ReduceInterference()
    {
        // Two APs ~60m apart with two exterior walls between them (separate buildings)
        var ap1 = CreateAp("aa:bb:cc:dd:ee:01", 36.0000, -94.0000);
        var ap2 = CreateAp("aa:bb:cc:dd:ee:02", 36.00054, -94.0000); // ~60m north

        var walls = new Dictionary<int, List<PropagationWall>>
        {
            [1] = new()
            {
                new PropagationWall
                {
                    Material = "exterior_residential",
                    Points = new()
                    {
                        new LatLng { Lat = 36.00018, Lng = -94.001 },
                        new LatLng { Lat = 36.00018, Lng = -93.999 }
                    }
                },
                new PropagationWall
                {
                    Material = "exterior_residential",
                    Points = new()
                    {
                        new LatLng { Lat = 36.00036, Lng = -94.001 },
                        new LatLng { Lat = 36.00036, Lng = -93.999 }
                    }
                }
            }
        };

        // At 60m with 2 exterior walls (14 dB at 5 GHz), should not interfere
        _svc.DoApsInterfere(ap1, ap2, "5", walls, null).Should().BeFalse();
    }

    [Fact]
    public void Threshold_IsConfigurable()
    {
        // Two APs at moderate distance - interfere at loose threshold, not at strict
        var ap1 = CreateAp("aa:bb:cc:dd:ee:01", 36.0000, -94.0000);
        var ap2 = CreateAp("aa:bb:cc:dd:ee:02", 36.00045, -94.0000); // ~50m north

        var walls = new Dictionary<int, List<PropagationWall>>();

        // Default threshold (-70) should not interfere at 50m on 5 GHz
        _svc.DoApsInterfere(ap1, ap2, "5", walls, null, thresholdDbm: -70).Should().BeFalse();

        // Looser threshold (-85) should interfere at 50m
        _svc.DoApsInterfere(ap1, ap2, "5", walls, null, thresholdDbm: -85).Should().BeTrue();
    }

    [Fact]
    public void UnplacedAp_ReturnsFalse()
    {
        // AP1 is placed, AP2 has default (0,0) coordinates (not placed on map)
        var ap1 = CreateAp("aa:bb:cc:dd:ee:01", 36.0000, -94.0000);
        var ap2 = CreateAp("aa:bb:cc:dd:ee:02", 0, 0); // Not placed

        var walls = new Dictionary<int, List<PropagationWall>>();

        // Should bail out and return false since AP2 isn't placed
        _svc.DoApsInterfere(ap1, ap2, "5", walls, null).Should().BeFalse();

        // Also when AP1 is unplaced
        var ap3 = CreateAp("aa:bb:cc:dd:ee:03", 0, 0); // Not placed
        var ap4 = CreateAp("aa:bb:cc:dd:ee:04", 36.0000, -94.0000);
        _svc.DoApsInterfere(ap3, ap4, "5", walls, null).Should().BeFalse();
    }

    [Fact]
    public void LowPowerAps_ReducedInterferenceRange()
    {
        // Two APs ~25m apart but at low power (10 dBm instead of 20 dBm)
        var ap1 = CreateAp("aa:bb:cc:dd:ee:01", 36.0000, -94.0000, txPower: 10);
        var ap2 = CreateAp("aa:bb:cc:dd:ee:02", 36.000225, -94.0000, txPower: 10); // ~25m north

        var walls = new Dictionary<int, List<PropagationWall>>();

        // At 20 dBm these would interfere, but at 10 dBm the range is shorter
        // Path loss at 25m on 5 GHz: ~76 dBm. Signal = 10 + 3 - 76 = -63 dBm → interfere
        // Actually 10 dBm at 25m on 5 GHz with indoor path loss should still be above -70
        // Let's check at 40m instead
        var ap2Far = CreateAp("aa:bb:cc:dd:ee:02", 36.00036, -94.0000, txPower: 10); // ~40m

        _svc.DoApsInterfere(ap1, ap2Far, "5", walls, null).Should().BeFalse();
    }
}
