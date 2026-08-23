using FluentAssertions;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

public class QuectelAtParserTests
{
    private const string TestHost = "192.0.2.20";
    private const string TestName = "GL-iNet Router";
    private const string TestModel = "GL-iNet";

    #region NR5G-SA Mode

    [Fact]
    public void Parse_Nr5gSa_ParsesAllFields()
    {
        var output = @"
AT+QENG=""servingcell""
+QENG: ""servingcell"",""NOCONN"",""NR5G-SA"",""FDD"",""001"",""01"",""1A2B3C"",286,""1234"",125530,""n71"",20,-106,-19,-8.0,15,0
OK";

        var stats = QuectelAtParser.Parse(output, TestHost, TestName, TestModel);

        stats.Should().NotBeNull();
        stats!.Nr5g.Should().NotBeNull();
        stats.Nr5g!.Rsrp.Should().Be(-106);
        stats.Nr5g.Rsrq.Should().Be(-19);
        stats.Nr5g.Snr.Should().Be(-8.0);
        stats.Lte.Should().BeNull();
        stats.NetworkMode.Should().Be(CellularNetworkMode.Nr5gSa);
        stats.CarrierMcc.Should().Be("001");
        stats.CarrierMnc.Should().Be("01");
        stats.ServingCell.Should().NotBeNull();
        stats.ServingCell!.PhysicalCellId.Should().Be(286);
        stats.ServingCell.GlobalCellId.Should().Be("1A2B3C");
        stats.ServingCell.Tac.Should().Be("1234");
        stats.ActiveBand.Should().NotBeNull();
        stats.ActiveBand!.RadioInterface.Should().Be("5gnr");
        stats.ActiveBand.BandClass.Should().Be("n71");
        stats.ActiveBand.Channel.Should().Be(125530);
        stats.ActiveBand.BandwidthMhz.Should().Be(20);
    }

    #endregion

    #region LTE Mode

    [Fact]
    public void Parse_Lte_ParsesAllFields()
    {
        var output = @"
+QENG: ""servingcell"",""NOCONN"",""LTE"",""FDD"",001,01,""0A1B2C3"",286,700,2,5,20,""1234"",-99,-10,-68,19,12,30,0
OK";

        var stats = QuectelAtParser.Parse(output, TestHost, TestName, TestModel);

        stats.Should().NotBeNull();
        stats!.Lte.Should().NotBeNull();
        stats.Lte!.Rsrp.Should().Be(-99);
        stats.Lte.Rsrq.Should().Be(-10);
        stats.Lte.Rssi.Should().Be(-68);
        stats.Lte.Snr.Should().Be(19);
        stats.Nr5g.Should().BeNull();
        stats.NetworkMode.Should().Be(CellularNetworkMode.Lte);
        stats.CarrierMcc.Should().Be("001");
        stats.CarrierMnc.Should().Be("01");
        stats.ServingCell.Should().NotBeNull();
        stats.ServingCell!.PhysicalCellId.Should().Be(286);
        stats.ActiveBand.Should().NotBeNull();
        stats.ActiveBand!.RadioInterface.Should().Be("lte");
        stats.ActiveBand.BandClass.Should().Be("eutran-2");
        stats.ActiveBand.Channel.Should().Be(700);
    }

    #endregion

    #region NR5G-NSA (EN-DC) Mode

    [Fact]
    public void Parse_Nr5gNsa_DualConnectivity_ParsesBothLines()
    {
        var output = @"
+QENG: ""servingcell"",""NOCONN""
+QENG: ""LTE"",""FDD"",001,01,""0A1B2C3"",286,700,2,5,20,""1234"",-85,-8,-55,22,15,30,0
+QENG: ""NR5G-NSA"",001,01,400,-92,18,-11,627264,""n77"",100,1
OK";

        var stats = QuectelAtParser.Parse(output, TestHost, TestName, TestModel);

        stats.Should().NotBeNull();
        stats!.Lte.Should().NotBeNull();
        stats.Lte!.Rsrp.Should().Be(-85);
        stats.Lte.Rsrq.Should().Be(-8);
        stats.Lte.Snr.Should().Be(22);
        stats.Nr5g.Should().NotBeNull();
        stats.Nr5g!.Rsrp.Should().Be(-92);
        stats.Nr5g.Snr.Should().Be(18);
        stats.Nr5g.Rsrq.Should().Be(-11);
        stats.NetworkMode.Should().Be(CellularNetworkMode.Nr5gNsa);
        // NR band should override LTE band as primary
        stats.ActiveBand.Should().NotBeNull();
        stats.ActiveBand!.RadioInterface.Should().Be("5gnr");
        stats.ActiveBand.BandClass.Should().Be("n77");
        stats.ActiveBand.Channel.Should().Be(627264);
        stats.ActiveBand.BandwidthMhz.Should().Be(100);
    }

    #endregion

    #region WCDMA (3G Fallback)

    [Fact]
    public void Parse_Wcdma_MapsRscpToRsrp()
    {
        var output = @"
+QENG: ""servingcell"",""NOCONN"",""WCDMA"",001,01,""1234"",""0A1B2C3"",10713,286,1,-85,-12,8,16,0,0,0
OK";

        var stats = QuectelAtParser.Parse(output, TestHost, TestName, TestModel);

        stats.Should().NotBeNull();
        stats!.Lte.Should().NotBeNull();
        stats.Lte!.Rsrp.Should().Be(-85); // RSCP mapped to RSRP
        stats.Lte.Rsrq.Should().Be(-12);  // Ec/Io mapped to RSRQ
        stats.CarrierMcc.Should().Be("001");
        stats.CarrierMnc.Should().Be("01");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Parse_EmptyInput_ReturnsNull()
    {
        QuectelAtParser.Parse("", TestHost, TestName, TestModel).Should().BeNull();
        QuectelAtParser.Parse(null!, TestHost, TestName, TestModel).Should().BeNull();
    }

    [Fact]
    public void Parse_NoQengLines_ReturnsNull()
    {
        var output = "OK\nERROR\n";
        QuectelAtParser.Parse(output, TestHost, TestName, TestModel).Should().BeNull();
    }

    [Fact]
    public void Parse_InvalidSignalValues_HandlesGracefully()
    {
        var output = @"+QENG: ""servingcell"",""NOCONN"",""NR5G-SA"",""FDD"",""001"",""01"",""1A2B3C"",286,""1234"",125530,""n71"",20,-32768,-32768,-32768,15,0";

        var stats = QuectelAtParser.Parse(output, TestHost, TestName, TestModel);
        // -32768 is Quectel's "not available" sentinel
        stats.Should().BeNull(); // No valid signal data
    }

    [Fact]
    public void Parse_HexCellId_ParsesCorrectly()
    {
        var output = @"+QENG: ""servingcell"",""NOCONN"",""NR5G-SA"",""FDD"",""001"",""01"",""0x1A2B3C"",286,""1234"",125530,""n71"",20,-106,-19,-8.0,15,0";

        var stats = QuectelAtParser.Parse(output, TestHost, TestName, TestModel);
        stats.Should().NotBeNull();
    }

    [Fact]
    public void Parse_BandNormalization_LteBandNumber()
    {
        var output = @"+QENG: ""servingcell"",""NOCONN"",""LTE"",""FDD"",001,01,""0A1B2C3"",286,700,71,5,20,""1234"",-99,-10,-68,19,12,30,0";

        var stats = QuectelAtParser.Parse(output, TestHost, TestName, TestModel);
        stats!.ActiveBand!.BandClass.Should().Be("eutran-71");
    }

    [Fact]
    public void Parse_BandNormalization_NrBandWithPrefix()
    {
        var output = @"+QENG: ""servingcell"",""NOCONN"",""NR5G-SA"",""FDD"",""001"",""01"",""1A2B3C"",286,""1234"",627264,""N77"",100,-92,-11,18.0,1,0";

        var stats = QuectelAtParser.Parse(output, TestHost, TestName, TestModel);
        stats!.ActiveBand!.BandClass.Should().Be("n77");
    }

    [Fact]
    public void Parse_SetsModemMetadata()
    {
        var output = @"+QENG: ""servingcell"",""NOCONN"",""LTE"",""FDD"",001,01,""0A1B2C3"",286,700,2,5,20,""1234"",-99,-10,-68,19,12,30,0";

        var stats = QuectelAtParser.Parse(output, TestHost, TestName, TestModel);
        stats!.ModemHost.Should().Be(TestHost);
        stats.ModemName.Should().Be(TestName);
        stats.ModemModel.Should().Be(TestModel);
    }

    [Fact]
    public void Parse_WithEchoAndOk_IgnoresNonQengLines()
    {
        var output = @"
AT+QENG=""servingcell""

+QENG: ""servingcell"",""NOCONN"",""LTE"",""FDD"",001,01,""0A1B2C3"",286,700,2,5,20,""1234"",-99,-10,-68,19,12,30,0

OK
";

        var stats = QuectelAtParser.Parse(output, TestHost, TestName, TestModel);
        stats.Should().NotBeNull();
        stats!.Lte.Should().NotBeNull();
    }

    #endregion

    #region Bandwidth enum (real device)

    // Sample from a GL-E5800 with an SoC-integrated RG650V-NA. Cell identity is replaced
    // with test values; signal and bandwidth fields are as reported. The router's own
    // cellular ubus service called the NR leg 90 MHz, and AT+QCAINFO put the LTE anchor
    // at 75 resource blocks, which is 15 MHz.
    private const string Rg650vNsaOutput = @"
+QENG: ""servingcell"",""NOCONN""
+QENG: ""LTE"",""FDD"",310,260,""0A1B2C3"",438,975,2,4,4,""1234"",-95,-7,-68,20,15,150,-
+QENG: ""NR5G-NSA"",310,260,46,-81,34,-10,502110,41,11,1

OK
";

    [Fact]
    public void Parse_Nr5gNsa_MapsBandwidthIndexToMhz()
    {
        var stats = QuectelAtParser.Parse(Rg650vNsaOutput, TestHost, TestName, TestModel);

        stats.Should().NotBeNull();
        stats!.ActiveBand.Should().NotBeNull();
        stats.ActiveBand!.BandClass.Should().Be("n41");
        stats.ActiveBand.BandwidthMhz.Should().Be(90);
    }

    [Fact]
    public void Parse_Lte_MapsBandwidthIndexToMhz()
    {
        // The NSA line overrides ActiveBand with the NR leg, so read the LTE anchor
        // on its own to reach the LTE bandwidth field.
        var lteOnly = @"
+QENG: ""servingcell"",""NOCONN""
+QENG: ""LTE"",""FDD"",310,260,""0A1B2C3"",438,975,2,4,4,""1234"",-95,-7,-68,20,15,150,-

OK
";

        var stats = QuectelAtParser.Parse(lteOnly, TestHost, TestName, TestModel);

        stats.Should().NotBeNull();
        stats!.ActiveBand.Should().NotBeNull();
        stats.ActiveBand!.BandClass.Should().Be("eutran-2");
        stats.ActiveBand.BandwidthMhz.Should().Be(15);
    }

    [Fact]
    public void Parse_Nr5gNsa_ReadsSignalFromBothLegs()
    {
        var stats = QuectelAtParser.Parse(Rg650vNsaOutput, TestHost, TestName, TestModel);

        stats.Should().NotBeNull();
        stats!.NetworkMode.Should().Be(CellularNetworkMode.Nr5gNsa);
        stats.Lte!.Rsrp.Should().Be(-95);
        stats.Lte.Rsrq.Should().Be(-7);
        stats.Lte.Snr.Should().Be(20);
        stats.Nr5g!.Rsrp.Should().Be(-81);
        stats.Nr5g.Rsrq.Should().Be(-10);
        stats.Nr5g.Snr.Should().Be(34);
        stats.ServingCell!.PhysicalCellId.Should().Be(438);
        stats.ServingCell.Earfcn.Should().Be(975);
    }

    #endregion

    #region Operator name

    [Fact]
    public void ParseOperator_ReturnsCarrierName()
    {
        var output = @"
+COPS: 0,0,""T-Mobile"",13

OK
";

        QuectelAtParser.ParseOperator(output).Should().Be("T-Mobile");
    }

    [Fact]
    public void ParseOperator_NotRegistered_ReturnsNull()
    {
        var output = @"
+COPS: 0

OK
";

        QuectelAtParser.ParseOperator(output).Should().BeNull();
    }

    [Fact]
    public void ParseOperator_NoResponse_ReturnsNull()
    {
        QuectelAtParser.ParseOperator("").Should().BeNull();
        QuectelAtParser.ParseOperator("ERROR").Should().BeNull();
    }

    #endregion

    #region Module firmware (AT+CGMR)

    [Fact]
    public void ParseRevisionResponse_ReturnsTheBareVersion()
    {
        var output = @"
AT+CGMR

QRM650VNA01ACR02A04G8G_OCPU_RGH_01.005.01.005

OK
";

        QuectelAtParser.ParseRevisionResponse(output)
            .Should().Be("QRM650VNA01ACR02A04G8G_OCPU_RGH_01.005.01.005");
    }

    [Fact]
    public void ParseRevisionResponse_StripsThePrefixWhenFirmwareUsesOne()
    {
        var output = @"+CGMR: EG25GGBR07A08M2G

OK
";

        QuectelAtParser.ParseRevisionResponse(output).Should().Be("EG25GGBR07A08M2G");
    }

    [Fact]
    public void ParseRevisionResponse_IgnoresBootNoticesFromAJustRebootedModem()
    {
        var output = @"
RDY
+CPIN: READY
+QUSIM: 1
QRM650VNA01ACR02A04G8G_OCPU_RGH_01.005.01.005

OK
";

        QuectelAtParser.ParseRevisionResponse(output)
            .Should().Be("QRM650VNA01ACR02A04G8G_OCPU_RGH_01.005.01.005");
    }

    [Fact]
    public void ParseRevisionResponse_UnsupportedOrEmpty_ReturnsNull()
    {
        QuectelAtParser.ParseRevisionResponse(null).Should().BeNull();
        QuectelAtParser.ParseRevisionResponse("").Should().BeNull();
        QuectelAtParser.ParseRevisionResponse(@"
ERROR
").Should().BeNull();
        QuectelAtParser.ParseRevisionResponse("+CME ERROR: 4").Should().BeNull();
    }

    #endregion
}
