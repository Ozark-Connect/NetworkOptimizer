using FluentAssertions;
using NetworkOptimizer.Monitoring;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

public class QmicliParserTests
{
    #region ParseSignalInfo Tests

    [Fact]
    public void ParseSignalInfo_LteSection_ParsesCorrectly()
    {
        // Arrange
        var output = @"
[/dev/cdc-wdm0] Signal info:
LTE:
	RSSI: '-51 dBm'
	RSRQ: '-9.0 dB'
	RSRP: '-79 dBm'
	SNR: '20.2 dB'
";

        // Act
        var (lte, nr5g) = QmicliParser.ParseSignalInfo(output);

        // Assert
        lte.Should().NotBeNull();
        lte!.Rssi.Should().Be(-51);
        lte.Rsrq.Should().Be(-9.0);
        lte.Rsrp.Should().Be(-79);
        lte.Snr.Should().Be(20.2);
        nr5g.Should().BeNull();
    }

    [Fact]
    public void ParseSignalInfo_Nr5gSection_ParsesCorrectly()
    {
        // Arrange
        var output = @"
[/dev/cdc-wdm0] Signal info:
5G:
	RSSI: '-65 dBm'
	RSRQ: '-7.5 dB'
	RSRP: '-85 dBm'
	SNR: '28.0 dB'
";

        // Act
        var (lte, nr5g) = QmicliParser.ParseSignalInfo(output);

        // Assert
        lte.Should().BeNull();
        nr5g.Should().NotBeNull();
        nr5g!.Rssi.Should().Be(-65);
        nr5g.Rsrq.Should().Be(-7.5);
        nr5g.Rsrp.Should().Be(-85);
        nr5g.Snr.Should().Be(28);
    }

    [Fact]
    public void ParseSignalInfo_BothLteAndNr5g_ParsesBoth()
    {
        // Arrange
        var output = @"
[/dev/cdc-wdm0] Signal info:
LTE:
	RSSI: '-51 dBm'
	RSRQ: '-9.0 dB'
	RSRP: '-79 dBm'
	SNR: '20.2 dB'
5G:
	RSSI: '-65 dBm'
	RSRQ: '-7.5 dB'
	RSRP: '-85 dBm'
	SNR: '28.0 dB'
";

        // Act
        var (lte, nr5g) = QmicliParser.ParseSignalInfo(output);

        // Assert
        lte.Should().NotBeNull();
        lte!.Rsrp.Should().Be(-79);
        nr5g.Should().NotBeNull();
        nr5g!.Rsrp.Should().Be(-85);
    }

    [Fact]
    public void ParseSignalInfo_EmptyOutput_ReturnsBothNull()
    {
        // Arrange
        var output = "";

        // Act
        var (lte, nr5g) = QmicliParser.ParseSignalInfo(output);

        // Assert
        lte.Should().BeNull();
        nr5g.Should().BeNull();
    }

    [Fact]
    public void ParseSignalInfo_NoSignalSections_ReturnsBothNull()
    {
        // Arrange
        var output = @"
[/dev/cdc-wdm0] Signal info:
Some other data
Not relevant
";

        // Act
        var (lte, nr5g) = QmicliParser.ParseSignalInfo(output);

        // Assert
        lte.Should().BeNull();
        nr5g.Should().BeNull();
    }

    [Fact]
    public void ParseSignalInfo_PartialLteData_ParsesAvailableMetrics()
    {
        // Arrange
        var output = @"
LTE:
	RSRP: '-92 dBm'
";

        // Act
        var (lte, nr5g) = QmicliParser.ParseSignalInfo(output);

        // Assert
        lte.Should().NotBeNull();
        lte!.Rsrp.Should().Be(-92);
        lte.Rssi.Should().BeNull(); // Not parsed, remains null
    }

    #endregion

    #region ParseServingSystem Tests

    [Fact]
    public void ParseServingSystem_FullOutput_ParsesCorrectly()
    {
        // Arrange
        var output = @"
[/dev/cdc-wdm0] Successfully got serving system:
	Registration state: 'registered'
	CS: 'attached'
	PS: 'attached'
	Selected network: '3gpp'
	Radio interfaces: '2'
		[0]: 'lte'
		[1]: 'nr5g'
	Current PLMN:
		MCC: '311'
		MNC: '480'
		Description: 'Verizon'
	Roaming status: 'off'
";

        // Act
        var (regState, carrier, mcc, mnc, isRoaming) = QmicliParser.ParseServingSystem(output);

        // Assert
        regState.Should().Be("registered");
        carrier.Should().Be("Verizon");
        mcc.Should().Be("311");
        mnc.Should().Be("480");
        isRoaming.Should().BeFalse();
    }

    [Fact]
    public void ParseServingSystem_RoamingOn_ReturnsTrue()
    {
        // Arrange
        var output = @"
	Registration state: 'registered'
	Roaming status: 'on'
";

        // Act
        var (_, _, _, _, isRoaming) = QmicliParser.ParseServingSystem(output);

        // Assert
        isRoaming.Should().BeTrue();
    }

    [Fact]
    public void ParseServingSystem_RoamingOff_ReturnsFalse()
    {
        // Arrange
        var output = @"
	Registration state: 'registered'
	Roaming status: 'off'
";

        // Act
        var (_, _, _, _, isRoaming) = QmicliParser.ParseServingSystem(output);

        // Assert
        isRoaming.Should().BeFalse();
    }

    [Fact]
    public void ParseServingSystem_EmptyOutput_ReturnsDefaults()
    {
        // Arrange
        var output = "";

        // Act
        var (regState, carrier, mcc, mnc, isRoaming) = QmicliParser.ParseServingSystem(output);

        // Assert
        regState.Should().BeEmpty();
        carrier.Should().BeEmpty();
        mcc.Should().BeEmpty();
        mnc.Should().BeEmpty();
        isRoaming.Should().BeFalse();
    }

    #endregion

    #region ParseCellLocationInfo Tests

    [Fact]
    public void ParseCellLocationInfo_WithServingCell_ParsesCorrectly()
    {
        // Arrange
        var output = @"
[/dev/cdc-wdm0] Successfully got cell location info
Intrafrequency LTE Info
	PLMN: '311480'
	Tracking Area Code: '12345'
	Global Cell ID: '123456789'
	EUTRA Absolute RF Channel Number: '66986' (B66)
	Serving Cell ID: '123'
	Cell [0]:
		Physical Cell ID: '123'
		RSRP: '-92.0'
		RSRQ: '-9.5'
		RSSI: '-65.0'
";

        // Act
        var (servingCell, neighbors) = QmicliParser.ParseCellLocationInfo(output);

        // Assert
        servingCell.Should().NotBeNull();
        servingCell!.Plmn.Should().Be("311480");
        servingCell.Tac.Should().Be("12345");
        servingCell.GlobalCellId.Should().Be("123456789");
        servingCell.Earfcn.Should().Be(66986);
        servingCell.BandDescription.Should().Be("B66");
        servingCell.PhysicalCellId.Should().Be(123);
        servingCell.IsServing.Should().BeTrue();
        servingCell.Signal.Should().NotBeNull();
        servingCell.Signal!.Rsrp.Should().Be(-92.0);
        servingCell.Signal.Rsrq.Should().Be(-9.5);
        servingCell.Signal.Rssi.Should().Be(-65.0);
    }

    [Fact]
    public void ParseCellLocationInfo_WithNeighborCells_ParsesAll()
    {
        // Arrange
        var output = @"
Intrafrequency LTE Info
	PLMN: '311480'
	EUTRA Absolute RF Channel Number: '66986' (B66)
	Serving Cell ID: '123'
Interfrequency LTE Info
	EUTRA Absolute RF Channel Number: '5110' (B2)
	Cell [0]:
		Physical Cell ID: '200'
		RSRP: '-100.0'
		RSRQ: '-12.0'
		RSSI: '-75.0'
	Cell [1]:
		Physical Cell ID: '201'
		RSRP: '-105.0'
		RSRQ: '-14.0'
";

        // Act
        var (_, neighbors) = QmicliParser.ParseCellLocationInfo(output);

        // Assert
        neighbors.Should().HaveCount(2);
        neighbors[0].PhysicalCellId.Should().Be(200);
        neighbors[0].Earfcn.Should().Be(5110);
        neighbors[0].BandDescription.Should().Be("B2");
        neighbors[0].Signal!.Rsrp.Should().Be(-100);
        neighbors[0].IsServing.Should().BeFalse();
        neighbors[1].PhysicalCellId.Should().Be(201);
    }

    [Fact]
    public void ParseCellLocationInfo_WithTimingAdvance_ParsesValue()
    {
        // Arrange
        var output = @"
Intrafrequency LTE Info
	PLMN: '311480'
	Serving Cell ID: '123'
	EUTRA Absolute RF Channel Number: '5110' (B2)
LTE Timing Advance: '3'
";

        // Act
        var (servingCell, _) = QmicliParser.ParseCellLocationInfo(output);

        // Assert
        servingCell.Should().NotBeNull();
        servingCell!.TimingAdvance.Should().Be(3);
    }

    [Fact]
    public void ParseCellLocationInfo_EmptyOutput_ReturnsNullAndEmptyList()
    {
        // Arrange
        var output = "";

        // Act
        var (servingCell, neighbors) = QmicliParser.ParseCellLocationInfo(output);

        // Assert
        servingCell.Should().BeNull();
        neighbors.Should().BeEmpty();
    }

    #endregion

    #region ParseRfBandInfo Tests

    [Fact]
    public void ParseRfBandInfo_LteBand_ParsesCorrectly()
    {
        // Arrange
        var output = @"
[/dev/cdc-wdm0] Successfully got RF band info
Band [0]:
	Radio Interface: 'lte'
	Active Band Class: 'eutran-66'
	Active Channel: '66986'
	Bandwidth: '20'
";

        // Act
        var band = QmicliParser.ParseRfBandInfo(output);

        // Assert
        band.Should().NotBeNull();
        band!.RadioInterface.Should().Be("lte");
        band.BandClass.Should().Be("eutran-66");
        band.Channel.Should().Be(66986);
        band.BandwidthMhz.Should().Be(20);
    }

    [Fact]
    public void ParseRfBandInfo_Nr5gBand_ParsesCorrectly()
    {
        // Arrange
        var output = @"
Band [0]:
	Radio Interface: 'nr5g'
	Active Band Class: 'nran-77'
	Active Channel: '635904'
	Bandwidth: '100'
";

        // Act
        var band = QmicliParser.ParseRfBandInfo(output);

        // Assert
        band.Should().NotBeNull();
        band!.RadioInterface.Should().Be("nr5g");
        band.BandClass.Should().Be("nran-77");
        band.Channel.Should().Be(635904);
        band.BandwidthMhz.Should().Be(100);
    }

    [Fact]
    public void ParseRfBandInfo_NoBandwidth_ParsesNullBandwidth()
    {
        // Arrange
        var output = @"
Band [0]:
	Radio Interface: 'lte'
	Active Band Class: 'eutran-66'
	Active Channel: '66986'
";

        // Act
        var band = QmicliParser.ParseRfBandInfo(output);

        // Assert
        band.Should().NotBeNull();
        band!.BandwidthMhz.Should().BeNull();
    }

    [Fact]
    public void ParseRfBandInfo_EmptyOutput_ReturnsNull()
    {
        // Arrange
        var output = "";

        // Act
        var band = QmicliParser.ParseRfBandInfo(output);

        // Assert
        band.Should().BeNull();
    }

    [Fact]
    public void ParseRfBandInfo_NoRadioInterface_ReturnsNull()
    {
        // Arrange
        var output = @"
Some unrelated output
Without radio interface info
";

        // Act
        var band = QmicliParser.ParseRfBandInfo(output);

        // Assert
        band.Should().BeNull();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ParseSignalInfo_NegativeDecimalValues_ParsesCorrectly()
    {
        // Arrange
        var output = @"
LTE:
	RSRP: '-92.5 dBm'
	RSRQ: '-10.75 dB'
	SNR: '-3.2 dB'
";

        // Act
        var (lte, _) = QmicliParser.ParseSignalInfo(output);

        // Assert
        lte.Should().NotBeNull();
        lte!.Rsrp.Should().Be(-92.5);
        lte.Rsrq.Should().Be(-10.75);
        lte.Snr.Should().Be(-3.2);
    }

    [Fact]
    public void ParseSignalInfo_WindowsLineEndings_ParsesCorrectly()
    {
        // Arrange - Windows-style \r\n line endings
        var output = "LTE:\r\n\tRSRP: '-90 dBm'\r\n";

        // Act
        var (lte, _) = QmicliParser.ParseSignalInfo(output);

        // Assert
        lte.Should().NotBeNull();
        lte!.Rsrp.Should().Be(-90);
    }

    [Fact]
    public void ParseServingSystem_VariousRoamingValues_ParsesCorrectly()
    {
        // Arrange - any value other than "off" should be roaming
        var outputRoaming = "\tRoaming status: 'roaming'\n";
        var outputHome = "\tRoaming status: 'home'\n"; // Still treated as roaming (not "off")

        // Act
        var (_, _, _, _, isRoaming1) = QmicliParser.ParseServingSystem(outputRoaming);
        var (_, _, _, _, isRoaming2) = QmicliParser.ParseServingSystem(outputHome);

        // Assert
        isRoaming1.Should().BeTrue();
        isRoaming2.Should().BeTrue(); // Anything not "off" is roaming
    }

    #endregion
}
