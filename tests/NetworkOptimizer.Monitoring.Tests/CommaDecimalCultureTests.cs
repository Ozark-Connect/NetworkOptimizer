using System.Globalization;
using FluentAssertions;
using NetworkOptimizer.Monitoring;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// Device output always uses a dot as the decimal separator, whatever the host's locale. These
/// run the parsers under de-DE, where "-92.5" would otherwise fail to parse or read as -925, so a
/// culture-sensitive parse shows up on any CI runner, not only on a comma-decimal machine.
/// </summary>
public class CommaDecimalCultureTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public CommaDecimalCultureTests() => CultureInfo.CurrentCulture = new CultureInfo("de-DE");

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    [Fact]
    public void Qmicli_SignalInfo_ReadsDotDecimals()
    {
        var (lte, _) = QmicliParser.ParseSignalInfo("""
            LTE:
            	RSRP: '-92.5 dBm'
            	RSRQ: '-10.75 dB'
            	SNR: '-3.2 dB'
            """);

        lte.Should().NotBeNull();
        lte!.Rsrp.Should().Be(-92.5);
        lte.Rsrq.Should().Be(-10.75);
        lte.Snr.Should().Be(-3.2);
    }

    [Fact]
    public void QuectelAt_ServingCell_ReadsDotDecimals()
    {
        var stats = QuectelAtParser.Parse("""
            AT+QENG="servingcell"
            +QENG: "servingcell","NOCONN","NR5G-SA","FDD","001","01","1A2B3C",286,"1234",125530,"n71",20,-106,-19,-8.5,15,0
            OK
            """, "192.0.2.1", "Test Modem", "RM520N");

        stats.Should().NotBeNull();
        stats!.Nr5g!.Snr.Should().Be(-8.5);
    }

    [Fact]
    public void Uiwwand_StringSignalValues_ReadDotDecimals()
    {
        var stats = UiwwandParser.Parse("""
            { "result": { "rat-mode-active": "LTE", "rsrp": "-99.5", "rsrq": "-10.5", "rssi": -68, "snr": "19.5" } }
            """, "192.0.2.10", "Test Modem", "U5G-Max");

        stats.Should().NotBeNull();
        stats!.Lte!.Rsrp.Should().Be(-99.5);
        stats.Lte.Snr.Should().Be(19.5);
    }
}
