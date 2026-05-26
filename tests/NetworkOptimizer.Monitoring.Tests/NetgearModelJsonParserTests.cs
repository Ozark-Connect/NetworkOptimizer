using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// Unit tests for <see cref="NetgearModelJsonParser"/>. Fixture JSON mirrors the structural
/// shape of NetgearWebApp's <c>/api/model.json</c> response (verified live against Nighthawk
/// M-series devices) but uses synthetic phyCid / channel / signal values so the tests do not
/// embed any real cell-tower identifiers or signal samples.
/// </summary>
public class NetgearModelJsonParserTests
{
    private static readonly ModemPollContext Context = new()
    {
        Id = 1,
        Name = "Test Hotspot",
        Host = "192.0.2.1",  // RFC 5737 documentation address
        ModemType = "netgear-nighthawk-hotspot",
    };

    [Fact]
    public void Parse_FullFixture_PopulatesAllSignalAndBandFields()
    {
        // Exercises the full happy path: diagInfo array with both LTE and NR5G,
        // PCC entries in both band-info arrays, all signal fields as unit-suffixed strings.
        var json = """
        {
          "general": { "deviceName": "Nighthawk Test Device" },
          "wwan": {
            "connection": "Connected",
            "connectionText": "Connected",
            "registerNetworkDisplay": "TestCarrier",
            "roaming": false,
            "diagInfo": [
              {
                "lteAttached": true,
                "nr5gAttached": true,
                "ltesigRssi": "-60 dBm",
                "ltesigRsrp": "-90 dBm",
                "ltesigRsrq": "-10 dB",
                "ltesigSnr": "5 dB",
                "nr5gsigRsrp": "-80 dBm",
                "nr5gsigRsrq": "-10 dB",
                "nr5gsigSnr": "10 dB"
              },
              {}
            ],
            "lteBandInfo": [
              { "isPcc": true, "band": 2, "channel": "100", "phyCid": "1", "dlBandwidth": "10MHz" }
            ],
            "nr5gBandInfo": [
              { "isPcc": true, "band": 71, "channel": "200", "phyCid": "2", "dlBandwidth": "20MHz" }
            ]
          }
        }
        """;

        var stats = ParseFromJson(json);

        stats.ModemModel.Should().Be("Nighthawk Test Device");
        stats.Carrier.Should().Be("TestCarrier");
        stats.RegistrationState.Should().Be("Connected");
        stats.IsRoaming.Should().BeFalse();

        stats.Lte.Should().NotBeNull();
        stats.Lte!.Rssi.Should().Be(-60);
        stats.Lte.Rsrp.Should().Be(-90);
        stats.Lte.Rsrq.Should().Be(-10);
        stats.Lte.Snr.Should().Be(5);

        stats.Nr5g.Should().NotBeNull();
        stats.Nr5g!.Rsrp.Should().Be(-80);
        stats.Nr5g.Rsrq.Should().Be(-10);
        stats.Nr5g.Snr.Should().Be(10);

        // PCC walking prefers NR5G when both RATs have RSRP - this is the 5G NSA case.
        stats.ActiveBand.Should().NotBeNull();
        stats.ActiveBand!.RadioInterface.Should().Be("nr5g");
        stats.ActiveBand.BandClass.Should().Be("n71");
        stats.ActiveBand.Channel.Should().Be(200);
        stats.ActiveBand.BandwidthMhz.Should().Be(20);

        stats.ServingCell.Should().NotBeNull();
        stats.ServingCell!.PhysicalCellId.Should().Be(2);
        stats.ServingCell.Earfcn.Should().Be(200);
        stats.ServingCell.IsServing.Should().BeTrue();
    }

    [Fact]
    public void Parse_HighBandNr5gFixture_RecognizesLargeChannelNumbersAndBandwidth()
    {
        // High-band NR5G (n77 C-band style) has very large channel numbers (~6 figures)
        // and wider bandwidth (100MHz). Confirm the parser handles these cleanly.
        var json = """
        {
          "general": { "deviceName": "Nighthawk Test Device Pro" },
          "wwan": {
            "connectionText": "Connected",
            "registerNetworkDisplay": "OtherTestCarrier",
            "roaming": false,
            "diagInfo": [
              {
                "ltesigRssi": "-60 dBm",
                "ltesigRsrp": "-85 dBm",
                "ltesigRsrq": "-10 dB",
                "ltesigSnr": "8 dB",
                "nr5gsigRsrp": "-75 dBm",
                "nr5gsigRsrq": "-10 dB",
                "nr5gsigSnr": "15 dB"
              },
              {}
            ],
            "lteBandInfo": [
              { "isPcc": true, "band": 2, "channel": "300", "phyCid": "3", "dlBandwidth": "20MHz" }
            ],
            "nr5gBandInfo": [
              { "isPcc": true, "band": 77, "channel": "400000", "phyCid": "4", "dlBandwidth": "100MHz" }
            ]
          }
        }
        """;

        var stats = ParseFromJson(json);

        stats.Carrier.Should().Be("OtherTestCarrier");
        stats.ActiveBand!.BandClass.Should().Be("n77");
        stats.ActiveBand.Channel.Should().Be(400000);
        stats.ActiveBand.BandwidthMhz.Should().Be(100);
        stats.ServingCell!.PhysicalCellId.Should().Be(4);
        stats.Nr5g!.Snr.Should().Be(15);
        stats.Lte!.Rsrp.Should().Be(-85);
    }

    [Fact]
    public void Parse_StripsUnitSuffixesFromAllNumericStrings()
    {
        // Every signal value comes back as a string with a unit suffix - dBm for power, dB for ratios.
        // The parser must strip these before parsing to double.
        var json = """
        {
          "wwan": {
            "diagInfo": [
              {
                "ltesigRssi": "-50.5 dBm",
                "ltesigRsrp": "-90 dBm",
                "ltesigRsrq": "-9.5 dB",
                "ltesigSnr": "12.3 dB"
              }
            ]
          }
        }
        """;

        var stats = ParseFromJson(json);

        stats.Lte.Should().NotBeNull();
        stats.Lte!.Rssi.Should().Be(-50.5);
        stats.Lte.Rsrp.Should().Be(-90);
        stats.Lte.Rsrq.Should().Be(-9.5);
        stats.Lte.Snr.Should().Be(12.3);
    }

    [Fact]
    public void Parse_DiagInfoMissing_FallsBackToWwanSignalStrength()
    {
        // Some firmware variants and the pre-bootstrap response don't expose diagInfo.
        // In that case wwan.signalStrength (flat: rssi/rsrp/rsrq/sinr as numbers) is the fallback.
        var json = """
        {
          "wwan": {
            "registerNetworkDisplay": "FallbackCarrier",
            "signalStrength": {
              "rssi": -70,
              "rsrp": -95,
              "rsrq": -13,
              "sinr": 4
            }
          }
        }
        """;

        var stats = ParseFromJson(json);

        stats.Carrier.Should().Be("FallbackCarrier");
        stats.Lte.Should().NotBeNull();
        stats.Lte!.Rssi.Should().Be(-70);
        stats.Lte.Rsrp.Should().Be(-95);
        stats.Lte.Rsrq.Should().Be(-13);
        stats.Lte.Snr.Should().Be(4);
        stats.Nr5g.Should().BeNull();
    }

    [Fact]
    public void Parse_DiagInfoIsEmptyArray_FallsBackGracefully()
    {
        // Defensive: if diagInfo is present but empty, we should not throw and should
        // fall through to signalStrength or end up with null signal.
        var json = """
        {
          "wwan": {
            "registerNetworkDisplay": "TestCarrier",
            "diagInfo": []
          }
        }
        """;

        var stats = ParseFromJson(json);

        stats.Carrier.Should().Be("TestCarrier");
        stats.Lte.Should().BeNull();
        stats.Nr5g.Should().BeNull();
    }

    [Fact]
    public void Parse_WwanMissing_ReturnsStatsWithIdentityOnly()
    {
        var json = """
        {
          "general": { "deviceName": "Nighthawk Test Device" }
        }
        """;

        var stats = ParseFromJson(json);

        stats.ModemHost.Should().Be(Context.Host);
        stats.ModemName.Should().Be(Context.Name);
        stats.ModemModel.Should().Be("Nighthawk Test Device");
        stats.Lte.Should().BeNull();
        stats.Nr5g.Should().BeNull();
        stats.ActiveBand.Should().BeNull();
        stats.ServingCell.Should().BeNull();
    }

    [Fact]
    public void Parse_DeviceNameMissing_FallsBackToContextModemType()
    {
        var json = """
        {
          "wwan": { "registerNetworkDisplay": "TestCarrier" }
        }
        """;

        var stats = ParseFromJson(json);

        stats.ModemModel.Should().Be(Context.ModemType);
    }

    [Fact]
    public void Parse_BandInfoWithoutPccEntry_ReturnsNullBand()
    {
        // If no entry has isPcc=true, the parser shouldn't guess by picking the first one.
        var json = """
        {
          "wwan": {
            "diagInfo": [{ "ltesigRsrp": "-90 dBm" }],
            "lteBandInfo": [
              { "isPcc": false, "band": 2, "channel": "100" },
              { "isPcc": false, "band": 5, "channel": "500" }
            ]
          }
        }
        """;

        var stats = ParseFromJson(json);

        stats.Lte!.Rsrp.Should().Be(-90);
        // BandInfo falls back to curBand string parsing; absent here, so null.
        stats.ActiveBand.Should().BeNull();
    }

    [Fact]
    public void Parse_LteOnlyMode_UsesLteBandInfoForActiveBand()
    {
        // 4G-only device: NR5G fields are absent, so the parser must pick LTE PCC.
        var json = """
        {
          "wwan": {
            "diagInfo": [
              {
                "ltesigRssi": "-70 dBm",
                "ltesigRsrp": "-92 dBm",
                "ltesigRsrq": "-12 dB",
                "ltesigSnr": "6 dB"
              }
            ],
            "lteBandInfo": [
              { "isPcc": true, "band": 4, "channel": "500", "phyCid": "10", "dlBandwidth": "20MHz" }
            ]
          }
        }
        """;

        var stats = ParseFromJson(json);

        stats.Nr5g.Should().BeNull();
        stats.ActiveBand!.RadioInterface.Should().Be("lte");
        stats.ActiveBand.BandClass.Should().Be("eutran-4");
        stats.ActiveBand.Channel.Should().Be(500);
        stats.ActiveBand.BandwidthMhz.Should().Be(20);
        stats.ServingCell!.PhysicalCellId.Should().Be(10);
    }

    [Fact]
    public void Parse_DlBandwidthInDifferentFormats_ExtractsNumericPortion()
    {
        var json = """
        {
          "wwan": {
            "diagInfo": [{ "ltesigRsrp": "-90 dBm" }],
            "lteBandInfo": [
              { "isPcc": true, "band": 2, "channel": "100", "dlBandwidth": "5MHz" }
            ]
          }
        }
        """;

        var stats = ParseFromJson(json);

        stats.ActiveBand!.BandwidthMhz.Should().Be(5);
    }

    [Fact]
    public void Parse_IsPccAsString_AlsoRecognized()
    {
        // Some Netgear firmware versions emit "isPcc": "true" (string) instead of true (bool).
        var json = """
        {
          "wwan": {
            "diagInfo": [{ "ltesigRsrp": "-90 dBm" }],
            "lteBandInfo": [
              { "isPcc": "true", "band": 7, "channel": "700", "phyCid": "20" }
            ]
          }
        }
        """;

        var stats = ParseFromJson(json);

        stats.ActiveBand!.BandClass.Should().Be("eutran-7");
        stats.ServingCell!.PhysicalCellId.Should().Be(20);
    }

    [Fact]
    public void Parse_CarrierFallsBackToServiceProviderName_WhenRegisterNetworkDisplayMissing()
    {
        var json = """
        {
          "general": { "serviceProviderName": "FallbackCarrier" },
          "wwan": { "diagInfo": [{ "ltesigRsrp": "-90 dBm" }] }
        }
        """;

        var stats = ParseFromJson(json);

        stats.Carrier.Should().Be("FallbackCarrier");
    }

    [Fact]
    public void Parse_RoamingTrue_PropagatesToStats()
    {
        var json = """
        {
          "wwan": {
            "roaming": true,
            "diagInfo": [{ "ltesigRsrp": "-100 dBm" }]
          }
        }
        """;

        var stats = ParseFromJson(json);

        stats.IsRoaming.Should().BeTrue();
    }

    [Fact]
    public void Parse_AllSignalFieldsNull_ReturnsNullSignalInfo()
    {
        // Defensive: a diagInfo entry with only non-signal flags should not produce a SignalInfo
        // with all-null fields - it should produce null, so downstream logic treats it as "no signal".
        var json = """
        {
          "wwan": {
            "diagInfo": [{ "lteAttached": true, "nr5gAttached": false }]
          }
        }
        """;

        var stats = ParseFromJson(json);

        stats.Lte.Should().BeNull();
        stats.Nr5g.Should().BeNull();
    }

    private static CellularModemStats ParseFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return NetgearModelJsonParser.Parse(doc.RootElement, Context);
    }
}
