using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// Unit tests for <see cref="ZyxelCellwanParser"/>. Fixtures follow the <c>cellwan_status</c> object
/// as Zyxel's GPL DAL source builds it and as NR730x units report it, with synthetic carrier, cell,
/// and signal values.
/// </summary>
public class ZyxelCellwanParserTests
{
    private static readonly ModemPollContext Context = new()
    {
        Id = 1,
        Name = "Test CPE",
        Host = "192.0.2.1",  // RFC 5737 documentation address
        ModemType = "Zyxel CPE",
    };

    // An EN-DC session: LTE anchor in INTF_*, the NR leg in NSA_*.
    private const string NsaCellwan = """
        {
          "CELL_Roaming_Enable": false,
          "INTF_Status": "Up",
          "INTF_Current_Access_Technology": "NR5G-NSA",
          "INTF_Network_In_Use": "Current_Test Carrier_NR5G-NSA_00101",
          "INTF_RSSI": -60,
          "INTF_Current_Band": "LTE_BC28",
          "INTF_Cell_ID": 1234567,
          "INTF_PhyCell_ID": 123,
          "INTF_Uplink_Bandwidth": 3,
          "INTF_Downlink_Bandwidth": 5,
          "INTF_RFCN": 9410,
          "INTF_RSRP": -88,
          "INTF_RSRQ": -12,
          "INTF_TAC": 4660,
          "INTF_SINR": 11,
          "INTF_CQI": 10,
          "NSA_Enable": true,
          "NSA_MCC": "001",
          "NSA_MNC": "01",
          "NSA_PhyCellID": 116,
          "NSA_RFCN": 632448,
          "NSA_Band": "N78",
          "NSA_DownlinkBandwidth": "100M",
          "NSA_RSSI": -70,
          "NSA_RSRP": -104,
          "NSA_RSRQ": -11,
          "NSA_SINR": 13,
          "SCC_Info": []
        }
        """;

    private const string DeviceInfo = """
        { "Manufacturer": "Zyxel", "ModelName": "Carrier 5G Router", "ProductClass": "NR7302", "SoftwareVersion": "V1.00(ABCD.1)C0" }
        """;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CellularModemStats Parse(string cellwan, string? deviceInfo = DeviceInfo) =>
        ZyxelCellwanParser.Parse(Json(cellwan), deviceInfo == null ? null : Json(deviceInfo), Context);

    private static string With(string json, string oldValue, string newValue)
    {
        json.Should().Contain(oldValue, "the fixture edit must hit its target");
        return json.Replace(oldValue, newValue);
    }

    // ----- Parse: radio -----

    [Fact]
    public void Parse_Nsa_SplitsLteAnchorAndNrLeg()
    {
        var stats = Parse(NsaCellwan);

        stats.Lte.Should().NotBeNull();
        stats.Lte!.Rsrp.Should().Be(-88);
        stats.Lte.Rsrq.Should().Be(-12);
        stats.Lte.Rssi.Should().Be(-60);
        stats.Lte.Snr.Should().Be(11);

        stats.Nr5g.Should().NotBeNull();
        stats.Nr5g!.Rsrp.Should().Be(-104);
        stats.Nr5g.Rsrq.Should().Be(-11);
        stats.Nr5g.Rssi.Should().Be(-70);
        stats.Nr5g.Snr.Should().Be(13);

        stats.NetworkMode.Should().Be(CellularNetworkMode.Nr5gNsa);
    }

    [Fact]
    public void Parse_Nsa_ActiveBandIsTheNrLeg()
    {
        var stats = Parse(NsaCellwan);

        stats.ActiveBand.Should().NotBeNull();
        stats.ActiveBand!.RadioInterface.Should().Be("nr5g");
        stats.ActiveBand.BandClass.Should().Be("n78");
        stats.ActiveBand.Channel.Should().Be(632448);
        stats.ActiveBand.BandwidthMhz.Should().Be(100);
    }

    [Fact]
    public void Parse_NsaDownlinkBandwidth_FallsBackToNsaDlBw()
    {
        var cellwan = With(NsaCellwan, "\"NSA_DownlinkBandwidth\": \"100M\"", "\"NSA_DL_BW\": \"60M\"");

        Parse(cellwan).ActiveBand!.BandwidthMhz.Should().Be(60);
    }

    [Fact]
    public void Parse_LteOnly_HasNoNrAndUsesTheLteBand()
    {
        var cellwan = With(NsaCellwan, "\"NSA_Enable\": true", "\"NSA_Enable\": false");

        var stats = Parse(cellwan);

        stats.Nr5g.Should().BeNull();
        stats.NetworkMode.Should().Be(CellularNetworkMode.Lte);
        stats.ActiveBand!.RadioInterface.Should().Be("lte");
        stats.ActiveBand.BandClass.Should().Be("eutran-28");
        stats.ActiveBand.Channel.Should().Be(9410);
        stats.ActiveBand.BandwidthMhz.Should().Be(20);
    }

    [Fact]
    public void Parse_NsaSentinelsWithoutEnableFlag_HaveNoNr()
    {
        // Some builds omit NSA_Enable; an unmeasured NR leg still reads as the RSRP sentinel.
        var cellwan = With(NsaCellwan, "\"NSA_Enable\": true,", "")
            .Replace("\"NSA_RSRP\": -104", "\"NSA_RSRP\": -140");

        Parse(cellwan).Nr5g.Should().BeNull();
    }

    [Fact]
    public void Parse_Standalone_PutsThePrimaryCellOnNr()
    {
        var cellwan = With(NsaCellwan, "\"INTF_Current_Access_Technology\": \"NR5G-NSA\"", "\"INTF_Current_Access_Technology\": \"NR5G-SA\"")
            .Replace("\"INTF_Current_Band\": \"LTE_BC28\"", "\"INTF_Current_Band\": \"N78\"")
            .Replace("\"INTF_Network_In_Use\": \"Current_Test Carrier_NR5G-NSA_00101\"", "\"INTF_Network_In_Use\": \"Current_Test Carrier_NR5G-SA_00101\"");

        var stats = Parse(cellwan);

        stats.Lte.Should().BeNull();
        stats.Nr5g!.Rsrp.Should().Be(-88);
        stats.NetworkMode.Should().Be(CellularNetworkMode.Nr5gSa);
        stats.ActiveBand!.BandClass.Should().Be("n78");
        stats.ActiveBand.Channel.Should().Be(9410);
        stats.Carrier.Should().Be("Test Carrier");
    }

    [Theory]
    [InlineData("\"INTF_RSRP\": -88", "\"INTF_RSRP\": -140")]
    [InlineData("\"INTF_RSRP\": -88", "\"INTF_RSRP\": 0")]
    [InlineData("\"INTF_RSRP\": -88", "\"INTF_RSRP\": \"\"")]
    public void Parse_UnmeasuredRsrp_HasNoLteSignal(string oldValue, string newValue)
    {
        var stats = Parse(With(NsaCellwan, oldValue, newValue));

        stats.Lte.Should().BeNull();
        stats.ServingCell!.Signal.Should().BeNull();
        stats.ServingCell.PhysicalCellId.Should().Be(123);
    }

    [Fact]
    public void Parse_Sentinels_BecomeNull()
    {
        var cellwan = With(NsaCellwan, "\"INTF_RSRQ\": -12", "\"INTF_RSRQ\": -240")
            .Replace("\"INTF_RSSI\": -60", "\"INTF_RSSI\": -120")
            .Replace("\"INTF_SINR\": 11", "\"INTF_SINR\": -20");

        var lte = Parse(cellwan).Lte!;

        lte.Rsrp.Should().Be(-88);
        lte.Rsrq.Should().BeNull();
        lte.Rssi.Should().BeNull();
        lte.Snr.Should().BeNull();
    }

    [Fact]
    public void Parse_ImplausibleNrSinr_IsDropped()
    {
        // An NR SINR of 138 has been seen on an NSA leg that is not really attached.
        var cellwan = With(NsaCellwan, "\"NSA_SINR\": 13", "\"NSA_SINR\": 138");

        var nr = Parse(cellwan).Nr5g!;

        nr.Rsrp.Should().Be(-104);
        nr.Snr.Should().BeNull();
    }

    [Fact]
    public void Parse_NumbersSentAsStrings_AreRead()
    {
        var cellwan = With(NsaCellwan, "\"INTF_RSRP\": -88", "\"INTF_RSRP\": \"-88\"")
            .Replace("\"INTF_RFCN\": 9410", "\"INTF_RFCN\": \"9410\"");

        var stats = Parse(With(cellwan, "\"NSA_Enable\": true", "\"NSA_Enable\": false"));

        stats.Lte!.Rsrp.Should().Be(-88);
        stats.ServingCell!.Earfcn.Should().Be(9410);
    }

    // ----- Parse: serving cell -----

    [Fact]
    public void Parse_ServingCell_CarriesIdentifiers()
    {
        var cell = Parse(NsaCellwan).ServingCell!;

        cell.IsServing.Should().BeTrue();
        cell.PhysicalCellId.Should().Be(123);
        cell.GlobalCellId.Should().Be("1234567");
        cell.Tac.Should().Be("4660");
        cell.Earfcn.Should().Be(9410);
        cell.Plmn.Should().Be("00101");
        cell.BandDescription.Should().Be(new BandInfo { BandClass = "eutran-28" }.BandName);
        cell.EnbId.Should().Be(1234567 >> 8);
    }

    [Fact]
    public void Parse_UnknownCellIdAndPci_AreLeftEmpty()
    {
        var cellwan = With(NsaCellwan, "\"INTF_Cell_ID\": 1234567", "\"INTF_Cell_ID\": -1")
            .Replace("\"INTF_PhyCell_ID\": 123", "\"INTF_PhyCell_ID\": -1");

        var cell = Parse(cellwan).ServingCell!;

        cell.GlobalCellId.Should().BeNull();
        cell.PhysicalCellId.Should().Be(0);
        cell.Signal.Should().NotBeNull();
    }

    [Fact]
    public void Parse_NoSignalNoCell_HasNoServingCell()
    {
        var cellwan = With(NsaCellwan, "\"INTF_RSRP\": -88", "\"INTF_RSRP\": -140")
            .Replace("\"INTF_Cell_ID\": 1234567", "\"INTF_Cell_ID\": -1")
            .Replace("\"INTF_PhyCell_ID\": 123", "\"INTF_PhyCell_ID\": -1");

        Parse(cellwan).ServingCell.Should().BeNull();
    }

    [Fact]
    public void Parse_Neighbors_AreReadWithSentinelsDropped()
    {
        var cellwan = With(NsaCellwan, "\"SCC_Info\": []", """
            "SCC_Info": [],
            "NBR_Info": [
              { "Enable": true, "NeighbourType": "intra", "PhyCellID": 200, "RFCN": 9410, "RSSI": -70, "RSRP": -95, "RSRQ": -14 },
              { "Enable": true, "NeighbourType": "inter", "PhyCellID": 201, "RFCN": 1300, "RSSI": -120, "RSRP": -140, "RSRQ": -240 },
              { "Enable": false, "PhyCellID": 202, "RSRP": -90 },
              { "Enable": true, "PhyCellID": -1, "RSRP": -90 }
            ]
            """);

        var neighbors = Parse(cellwan).NeighborCells;

        neighbors.Should().HaveCount(2);
        neighbors[0].PhysicalCellId.Should().Be(200);
        neighbors[0].Earfcn.Should().Be(9410);
        neighbors[0].Signal!.Rsrp.Should().Be(-95);
        neighbors[0].Signal!.Rsrq.Should().Be(-14);
        neighbors[0].IsServing.Should().BeFalse();
        neighbors[1].PhysicalCellId.Should().Be(201);
        neighbors[1].Signal.Should().BeNull();
    }

    [Fact]
    public void Parse_NoNeighborList_IsEmpty()
    {
        Parse(NsaCellwan).NeighborCells.Should().BeEmpty();
    }

    // ----- Parse: identity and carrier -----

    [Fact]
    public void Parse_CarrierAndPlmn_ComeFromNetworkInUseAndNsaCodes()
    {
        var stats = Parse(NsaCellwan);

        stats.Carrier.Should().Be("Test Carrier");
        stats.CarrierMcc.Should().Be("001");
        stats.CarrierMnc.Should().Be("01");
    }

    [Fact]
    public void Parse_WithoutNsaCodes_MccMncComeFromThePlmn()
    {
        var cellwan = With(NsaCellwan, "\"NSA_MCC\": \"001\"", "\"NSA_MCC\": \"\"")
            .Replace("\"NSA_MNC\": \"01\"", "\"NSA_MNC\": \"\"")
            .Replace("_NR5G-NSA_00101", "_NR5G-NSA_001010");

        var stats = Parse(cellwan);

        stats.CarrierMcc.Should().Be("001");
        stats.CarrierMnc.Should().Be("010");
        stats.ServingCell!.Plmn.Should().Be("001010");
    }

    [Fact]
    public void Parse_DeviceInfo_ProductClassWinsOverRebrandedModelName()
    {
        var stats = Parse(NsaCellwan);

        stats.ModemModel.Should().Be("NR7302");
        stats.SoftwareVersion.Should().Be("V1.00(ABCD.1)C0");
        stats.ModemHost.Should().Be("192.0.2.1");
        stats.ModemName.Should().Be("Test CPE");
    }

    [Fact]
    public void Parse_WithoutProductClass_UsesModelName()
    {
        var stats = Parse(NsaCellwan, """{ "ModelName": "NR7101" }""");

        stats.ModemModel.Should().Be("NR7101");
        stats.SoftwareVersion.Should().BeNull();
    }

    [Fact]
    public void Parse_WithoutDeviceInfo_UsesTheConfiguredType()
    {
        Parse(NsaCellwan, deviceInfo: null).ModemModel.Should().Be("Zyxel CPE");
    }

    [Theory]
    [InlineData("Up", "registered")]
    [InlineData("up", "registered")]
    [InlineData("Down", "Down")]
    public void Parse_InterfaceStatus_MapsToRegistrationState(string status, string expected)
    {
        var stats = Parse(With(NsaCellwan, "\"INTF_Status\": \"Up\"", $"\"INTF_Status\": \"{status}\""));

        stats.RegistrationState.Should().Be(expected);
    }

    [Fact]
    public void Parse_MissingStatus_LeavesRegistrationEmpty()
    {
        Parse(With(NsaCellwan, "\"INTF_Status\": \"Up\",", "")).RegistrationState.Should().BeEmpty();
    }

    // ----- ParseNetworkInUse -----

    [Theory]
    [InlineData("Current_Test Carrier - XX_NR5G-NSA_00101", "NR5G-NSA", "Test Carrier - XX", "00101")]
    [InlineData("Current_TEST TEST_NR-NSA EN-DC_00102", "NR-NSA EN-DC", "TEST TEST", "00102")]
    [InlineData("Current_TestNet_LTE_001010", "LTE-A", "TestNet", "001010")]
    [InlineData("Current_TestNet", "LTE", "TestNet", null)]
    [InlineData("TestNet_LTE_00101", "LTE", "TestNet", "00101")]
    public void ParseNetworkInUse_SplitsCarrierAndPlmn(string input, string tech, string carrier, string? plmn)
    {
        var (c, p) = ZyxelCellwanParser.ParseNetworkInUse(input, tech);

        c.Should().Be(carrier);
        p.Should().Be(plmn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseNetworkInUse_Empty_ReturnsNulls(string? input)
    {
        ZyxelCellwanParser.ParseNetworkInUse(input, "LTE").Should().Be(((string?)null, (string?)null));
    }

    // ----- ParseBand -----

    [Theory]
    [InlineData("LTE_BC7", "lte", "eutran-7")]
    [InlineData("LTE_BC1 (LTE 2100)", "lte", "eutran-1")]
    [InlineData("B20", "lte", "eutran-20")]
    [InlineData("N78", "nr5g", "n78")]
    [InlineData("n41", "nr5g", "n41")]
    [InlineData("NR5G_BAND77", "nr5g", "n77")]
    public void ParseBand_ReadsLteAndNrForms(string input, string rat, string bandClass)
    {
        var band = ZyxelCellwanParser.ParseBand(input);

        band.Should().NotBeNull();
        band!.Value.RadioInterface.Should().Be(rat);
        band.Value.BandClass.Should().Be(bandClass);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("UNKNOWN")]
    public void ParseBand_NoBand_ReturnsNull(string? input)
    {
        ZyxelCellwanParser.ParseBand(input).Should().BeNull();
    }

    // ----- ParseBandwidthMhz -----

    [Theory]
    [InlineData("0", null)]
    [InlineData("1", 3)]
    [InlineData("2", 5)]
    [InlineData("3", 10)]
    [InlineData("4", 15)]
    [InlineData("5", 20)]
    [InlineData("6", null)]
    [InlineData("-1", null)]
    [InlineData("\"60M\"", 60)]
    [InlineData("\"100 MHz\"", 100)]
    [InlineData("\"\"", null)]
    [InlineData("\"N/A\"", null)]
    public void ParseBandwidthMhz_ReadsIndexOrMegahertz(string raw, int? expected)
    {
        var source = Json($$"""{ "BW": {{raw}} }""");

        ZyxelCellwanParser.ParseBandwidthMhz(source, "BW").Should().Be(expected);
    }

    // ----- TryGetFirstObject -----

    [Fact]
    public void TryGetFirstObject_Success_ReturnsTheFirstObject()
    {
        var response = Json("""{ "result": "ZCFG_SUCCESS", "Object": [ { "INTF_Status": "Up" } ] }""");

        ZyxelCellwanParser.TryGetFirstObject(response, out var obj).Should().BeTrue();
        obj.GetProperty("INTF_Status").GetString().Should().Be("Up");
    }

    [Theory]
    [InlineData("""{ "result": "ZCFG_NO_SUCH_OBJECT", "Object": [ { "a": 1 } ] }""")]
    [InlineData("""{ "result": "ZCFG_SUCCESS", "Object": [] }""")]
    [InlineData("""{ "result": "ZCFG_SUCCESS" }""")]
    [InlineData("""{ "result": "ZCFG_SUCCESS", "Object": [ "text" ] }""")]
    [InlineData("""[ 1, 2 ]""")]
    public void TryGetFirstObject_FailureOrEmpty_ReturnsFalse(string json)
    {
        ZyxelCellwanParser.TryGetFirstObject(Json(json), out _).Should().BeFalse();
    }
}
