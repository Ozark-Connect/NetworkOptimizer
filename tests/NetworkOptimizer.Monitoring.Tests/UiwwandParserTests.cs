using FluentAssertions;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

public class UiwwandParserTests
{
    private const string TestHost = "192.0.2.10";
    private const string TestName = "Test Modem";
    private const string TestModel = "U5G-Max";

    #region Full Response Tests

    [Fact]
    public void Parse_LteMode_ParsesAllFields()
    {
        var json = @"{
            ""result"": {
                ""rat-mode-active"": ""LTE"",
                ""rssi"": -68,
                ""rsrq"": -10,
                ""rsrp"": -99,
                ""snr"": 19.0,
                ""signal-bars"": 3,
                ""signal-percent"": 80,
                ""has-coverage"": true,
                ""cell-id"": 12345678,
                ""pci"": 100,
                ""channel"": 700,
                ""band-class"": ""eutran-2"",
                ""registered-spn"": ""ExampleCarrier"",
                ""roaming"": false,
                ""mcc"": 1,
                ""mnc"": 1,
                ""mode"": ""5G"",
                ""5g-sa-mode"": false,
                ""ca-lte"": [
                    { ""primary"": true, ""band"": 2, ""dl-bw-mhz"": 20.0, ""ul-bw-mhz"": 20.0, ""dl-earfcn"": 700 },
                    { ""primary"": false, ""band"": 14, ""dl-bw-mhz"": 10.0, ""ul-bw-mhz"": 0.0, ""dl-earfcn"": 5330 }
                ],
                ""ca-nr"": []
            }
        }";

        var stats = UiwwandParser.Parse(json, TestHost, TestName, TestModel);

        stats.Should().NotBeNull();
        stats!.Lte.Should().NotBeNull();
        stats.Lte!.Rsrp.Should().Be(-99);
        stats.Lte.Rsrq.Should().Be(-10);
        stats.Lte.Rssi.Should().Be(-68);
        stats.Lte.Snr.Should().Be(19.0);
        stats.Nr5g.Should().BeNull();
        stats.NetworkMode.Should().Be(CellularNetworkMode.Lte);
        stats.Carrier.Should().Be("ExampleCarrier");
        stats.CarrierMcc.Should().Be("1");
        stats.CarrierMnc.Should().Be("1");
        stats.IsRoaming.Should().BeFalse();
        stats.RegistrationState.Should().Be("registered");
        stats.ActiveBand.Should().NotBeNull();
        stats.ActiveBand!.RadioInterface.Should().Be("lte");
        stats.ActiveBand.BandClass.Should().Be("eutran-2");
        stats.ActiveBand.Channel.Should().Be(700);
        stats.ActiveBand.BandwidthMhz.Should().Be(20);
        stats.ServingCell.Should().NotBeNull();
        stats.ServingCell!.GlobalCellId.Should().Be("12345678");
        stats.ServingCell.PhysicalCellId.Should().Be(100);
    }

    [Fact]
    public void Parse_5gSaMode_PopulatesNr5gOnly()
    {
        var json = @"{
            ""result"": {
                ""rat-mode-active"": ""5G"",
                ""rsrp"": -106,
                ""snr"": -8.0,
                ""rsrq"": -19,
                ""signal-bars"": 2,
                ""signal-percent"": 45,
                ""has-coverage"": true,
                ""channel"": 125530,
                ""band-class"": ""n71"",
                ""registered-spn"": ""TestCarrier"",
                ""roaming"": false,
                ""mcc"": 1,
                ""mnc"": 1,
                ""mode"": ""5G"",
                ""5g-sa-mode"": true,
                ""rsrp-nr"": -106,
                ""rsrq-nr"": -19,
                ""snr-nr"": -8.0,
                ""ca-nr"": [
                    { ""primary"": true, ""band"": 71, ""dl-bw-mhz"": 20.0, ""ul-bw-mhz"": 20.0, ""dl-arfcn"": 125530, ""ul-arfcn"": 125530 }
                ]
            }
        }";

        var stats = UiwwandParser.Parse(json, TestHost, TestName, "U5G Backup");

        stats.Should().NotBeNull();
        stats!.Nr5g.Should().NotBeNull();
        stats.Nr5g!.Rsrp.Should().Be(-106);
        stats.Nr5g.Rsrq.Should().Be(-19);
        stats.Nr5g.Snr.Should().Be(-8.0);
        stats.Lte.Should().BeNull();
        stats.NetworkMode.Should().Be(CellularNetworkMode.Nr5gSa);
        stats.Carrier.Should().Be("TestCarrier");
        stats.ActiveBand.Should().NotBeNull();
        stats.ActiveBand!.RadioInterface.Should().Be("5gnr");
        stats.ActiveBand.BandClass.Should().Be("n71");
        stats.ActiveBand.Channel.Should().Be(125530);
        stats.ActiveBand.BandwidthMhz.Should().Be(20);
    }

    [Fact]
    public void Parse_5gSaMode_WithoutNrSuffix_FallsBackToBaseFields()
    {
        var json = @"{
            ""result"": {
                ""rat-mode-active"": ""5G"",
                ""rsrp"": -95,
                ""rsrq"": -12,
                ""snr"": 15.0,
                ""has-coverage"": true,
                ""channel"": 125530,
                ""band-class"": ""n71"",
                ""registered-spn"": ""TestCarrier"",
                ""mcc"": 1,
                ""mnc"": 1,
                ""5g-sa-mode"": true
            }
        }";

        var stats = UiwwandParser.Parse(json, TestHost, TestName, TestModel);

        stats.Should().NotBeNull();
        stats!.Nr5g.Should().NotBeNull();
        stats.Nr5g!.Rsrp.Should().Be(-95);
        stats.Lte.Should().BeNull();
        stats.NetworkMode.Should().Be(CellularNetworkMode.Nr5gSa);
    }

    [Fact]
    public void Parse_NsaMode_PopulatesBothLteAndNr5g()
    {
        var json = @"{
            ""result"": {
                ""rat-mode-active"": ""5G"",
                ""rsrp"": -85,
                ""rsrq"": -8,
                ""snr"": 22.0,
                ""rssi"": -55,
                ""has-coverage"": true,
                ""channel"": 700,
                ""band-class"": ""eutran-2"",
                ""registered-spn"": ""TestCarrier2"",
                ""mcc"": 311,
                ""mnc"": 2,
                ""5g-sa-mode"": false,
                ""rsrp-nr"": -92,
                ""rsrq-nr"": -11,
                ""snr-nr"": 18.0,
                ""ca-lte"": [{ ""primary"": true, ""band"": 2, ""dl-bw-mhz"": 20.0 }],
                ""ca-nr"": [{ ""primary"": true, ""band"": 77, ""dl-bw-mhz"": 100.0 }]
            }
        }";

        var stats = UiwwandParser.Parse(json, TestHost, TestName, TestModel);

        stats.Should().NotBeNull();
        stats!.Lte.Should().NotBeNull();
        stats.Lte!.Rsrp.Should().Be(-85);
        stats.Nr5g.Should().NotBeNull();
        stats.Nr5g!.Rsrp.Should().Be(-92);
        stats.NetworkMode.Should().Be(CellularNetworkMode.Nr5gNsa);
    }

    #endregion

    #region Carrier Aggregation Tests

    [Fact]
    public void Parse_CaLte_ExtractsPrimaryBandwidth()
    {
        var json = @"{
            ""result"": {
                ""rat-mode-active"": ""LTE"",
                ""rsrp"": -90,
                ""has-coverage"": true,
                ""channel"": 700,
                ""band-class"": ""eutran-2"",
                ""registered-spn"": ""Test"",
                ""mcc"": 1, ""mnc"": 1,
                ""5g-sa-mode"": false,
                ""ca-lte"": [
                    { ""primary"": false, ""band"": 14, ""dl-bw-mhz"": 10.0 },
                    { ""primary"": true, ""band"": 2, ""dl-bw-mhz"": 20.0 }
                ]
            }
        }";

        var stats = UiwwandParser.Parse(json, TestHost, TestName, TestModel);
        stats!.ActiveBand!.BandwidthMhz.Should().Be(20);
    }

    [Fact]
    public void Parse_EmptyCaArrays_NoBandwidthSet()
    {
        var json = @"{
            ""result"": {
                ""rat-mode-active"": ""LTE"",
                ""rsrp"": -90,
                ""has-coverage"": true,
                ""channel"": 700,
                ""band-class"": ""eutran-2"",
                ""registered-spn"": ""Test"",
                ""mcc"": 1, ""mnc"": 1,
                ""5g-sa-mode"": false,
                ""ca-lte"": [],
                ""ca-nr"": []
            }
        }";

        var stats = UiwwandParser.Parse(json, TestHost, TestName, TestModel);
        stats!.ActiveBand!.BandwidthMhz.Should().BeNull();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Parse_InvalidJson_ReturnsNull()
    {
        UiwwandParser.Parse("not json", TestHost, TestName, TestModel).Should().BeNull();
    }

    [Fact]
    public void Parse_MissingResultObject_ReturnsNull()
    {
        UiwwandParser.Parse("{}", TestHost, TestName, TestModel).Should().BeNull();
    }

    [Fact]
    public void Parse_EmptyResult_ReturnsStatsWithNullSignal()
    {
        var json = @"{ ""result"": {} }";
        var stats = UiwwandParser.Parse(json, TestHost, TestName, TestModel);
        stats.Should().NotBeNull();
        stats!.Lte.Should().BeNull();
        stats.Nr5g.Should().BeNull();
    }

    [Fact]
    public void Parse_Roaming_SetsFlag()
    {
        var json = @"{
            ""result"": {
                ""rat-mode-active"": ""LTE"",
                ""rsrp"": -100,
                ""has-coverage"": true,
                ""roaming"": true,
                ""registered-spn"": ""Partner"",
                ""mcc"": 1, ""mnc"": 1,
                ""5g-sa-mode"": false
            }
        }";

        var stats = UiwwandParser.Parse(json, TestHost, TestName, TestModel);
        stats!.IsRoaming.Should().BeTrue();
    }

    [Fact]
    public void Parse_NoCoverage_SetsRegistrationState()
    {
        var json = @"{
            ""result"": {
                ""rat-mode-active"": ""LTE"",
                ""has-coverage"": false,
                ""registration-state"": 0,
                ""5g-sa-mode"": false
            }
        }";

        var stats = UiwwandParser.Parse(json, TestHost, TestName, TestModel);
        stats.Should().NotBeNull();
        stats!.RegistrationState.Should().Be("state-0");
    }

    [Fact]
    public void Parse_SetsModemMetadata()
    {
        var json = @"{
            ""result"": {
                ""rat-mode-active"": ""LTE"",
                ""rsrp"": -90,
                ""5g-sa-mode"": false
            }
        }";

        var stats = UiwwandParser.Parse(json, "10.0.0.1", "My Modem", "U5G-Max");
        stats!.ModemHost.Should().Be("10.0.0.1");
        stats.ModemName.Should().Be("My Modem");
        stats.ModemModel.Should().Be("U5G-Max");
    }

    #endregion

    #region Cell Tower Info Tests

    private const string CellTowerInfoJson = @"{
        ""result"": {
            ""lte"": {
                ""tac"": 24591,
                ""cell_id"": 12345678,
                ""pci"": 286,
                ""mcc"": 1,
                ""mnc"": 1,
                ""adv"": 5,
                ""earfcn"": 700,
                ""rsrp"": 40,
                ""rsrq"": 21,
                ""neighbor_measurements"": [
                    { ""pci"": 90, ""cell_id"": 12345692, ""earfcn"": 950, ""adv"": 6, ""rsrp"": 24, ""rsrq"": 2 },
                    { ""pci"": 174, ""cell_id"": 12345897, ""earfcn"": 1550, ""adv"": 5, ""rsrp"": 25, ""rsrq"": 2 }
                ]
            }
        }
    }";

    [Fact]
    public void ParseCellTowerInfo_PopulatesTimingAdvanceAndTac()
    {
        var stats = new CellularModemStats();

        UiwwandParser.ParseCellTowerInfo(CellTowerInfoJson, stats);

        stats.ServingCell.Should().NotBeNull();
        stats.ServingCell!.TimingAdvance.Should().Be(5);
        stats.ServingCell.Tac.Should().Be("24591");
        stats.ServingCell.GlobalCellId.Should().Be("12345678");
        stats.ServingCell.Earfcn.Should().Be(700);
        stats.ServingCell.IsServing.Should().BeTrue();
    }

    [Fact]
    public void ParseCellTowerInfo_PopulatesNeighborsWithoutRawSignal()
    {
        var stats = new CellularModemStats();

        UiwwandParser.ParseCellTowerInfo(CellTowerInfoJson, stats);

        stats.NeighborCells.Should().HaveCount(2);
        stats.NeighborCells[0].PhysicalCellId.Should().Be(90);
        stats.NeighborCells[0].GlobalCellId.Should().Be("12345692");
        stats.NeighborCells[0].Earfcn.Should().Be(950);
        stats.NeighborCells[0].TimingAdvance.Should().Be(6);
        stats.NeighborCells[0].Signal.Should().BeNull();
    }

    [Fact]
    public void ParseCellTowerInfo_KeepsPhysicalCellIdFromRadioStatus()
    {
        var radioStatusJson = @"{
            ""result"": {
                ""rat-mode-active"": ""5G"",
                ""rsrp"": -99,
                ""rsrp-nr"": -82,
                ""cell-id"": 12345678,
                ""pci"": 353,
                ""5g-sa-mode"": false
            }
        }";
        var stats = UiwwandParser.Parse(radioStatusJson, TestHost, TestName, TestModel);

        UiwwandParser.ParseCellTowerInfo(CellTowerInfoJson, stats!);

        stats!.ServingCell!.PhysicalCellId.Should().Be(353);
        stats.ServingCell.TimingAdvance.Should().Be(5);
    }

    [Fact]
    public void ParseCellTowerInfo_MalformedOrMissingLte_LeavesStatsUntouched()
    {
        var stats = new CellularModemStats();

        UiwwandParser.ParseCellTowerInfo("not json", stats);
        UiwwandParser.ParseCellTowerInfo(@"{""result"": {}}", stats);
        UiwwandParser.ParseCellTowerInfo(@"{""result"": {""lte"": []}}", stats);

        stats.ServingCell.Should().BeNull();
        stats.NeighborCells.Should().BeEmpty();
    }

    [Theory]
    [InlineData("98765432", 385802, 120)]
    [InlineData("12345678", 48225, 78)]
    public void CellInfo_DerivesSiteAndSectorFromCellId(string cellId, int expectedEnb, int expectedSector)
    {
        var cell = new CellInfo { GlobalCellId = cellId };

        cell.EnbId.Should().Be(expectedEnb);
        cell.SectorId.Should().Be(expectedSector);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void CellInfo_SiteAndSectorNullWhenCellIdUnusable(string? cellId)
    {
        var cell = new CellInfo { GlobalCellId = cellId };

        cell.EnbId.Should().BeNull();
        cell.SectorId.Should().BeNull();
    }

    #endregion

    #region EN-DC State Tests

    private const string SystemInfoOutput = @"[/dev/wwan0qmi0] Successfully got system info:
	LTE service:
		Registration restriction: 'unrestricted'
		5G NSA Available: '{0}'
		DCNR Restriction: '{1}'";

    [Theory]
    [InlineData("yes", "no", true, false)]
    [InlineData("no", "no", false, false)]
    [InlineData("no", "yes", false, true)]
    public void ParseSystemInfo_ReadsBothFlags(
        string nsa, string dcnr, bool expectedNsa, bool expectedDcnr)
    {
        var output = string.Format(SystemInfoOutput, nsa, dcnr);

        var (nsaAvailable, dcnrRestricted) = QmicliParser.ParseSystemInfo(output);

        nsaAvailable.Should().Be(expectedNsa);
        dcnrRestricted.Should().Be(expectedDcnr);
    }

    [Fact]
    public void ParseSystemInfo_FlagsAbsent_ReturnsNulls()
    {
        var (nsaAvailable, dcnrRestricted) = QmicliParser.ParseSystemInfo("no LTE service reported");

        nsaAvailable.Should().BeNull();
        dcnrRestricted.Should().BeNull();
    }

    [Fact]
    public void EnDc_FlagsMissing_IsUnknown()
    {
        new CellularModemStats().EnDc.Should().Be(EnDcState.Unknown);
        new CellularModemStats { Is5gNsaAvailable = false }.EnDc.Should().Be(EnDcState.Unknown);
    }

    [Fact]
    public void EnDc_PermittedButCellDoesNotOfferIt_IsAnchorMissing()
    {
        var stats = new CellularModemStats { Is5gNsaAvailable = false, IsDcnrRestricted = false };

        stats.EnDc.Should().Be(EnDcState.AnchorMissing);
    }

    [Fact]
    public void EnDc_NetworkRestricted_OutranksAnchorState()
    {
        var stats = new CellularModemStats { Is5gNsaAvailable = false, IsDcnrRestricted = true };

        stats.EnDc.Should().Be(EnDcState.NetworkRestricted);
    }

    [Fact]
    public void EnDc_CellOffersItButNoNrLegYet_IsAnchorCapable()
    {
        var stats = new CellularModemStats { Is5gNsaAvailable = true, IsDcnrRestricted = false };

        stats.EnDc.Should().Be(EnDcState.AnchorCapable);
    }

    [Fact]
    public void EnDc_NrLegAttached_IsAttached()
    {
        var stats = new CellularModemStats
        {
            Is5gNsaAvailable = true,
            IsDcnrRestricted = false,
            Nr5g = new SignalInfo { Rsrp = -82 },
        };

        stats.EnDc.Should().Be(EnDcState.Attached);
    }

    #endregion
}
