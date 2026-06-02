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
}
