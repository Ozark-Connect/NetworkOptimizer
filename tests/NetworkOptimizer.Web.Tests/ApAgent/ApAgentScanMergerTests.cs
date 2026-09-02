using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

public class ApAgentScanMergerTests
{
    private const string Ap = "aa:bb:cc:dd:ee:01";
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private const string ScanJson = """
    {
      "read_at": "2026-09-02T11:59:50Z",
      "collected_at": "2026-09-02T11:59:55Z",
      "radios": [
        {
          "name": "wifi1", "band": "5", "spectrum_at": "2026-09-02T11:58:00Z",
          "scan_table": [
            { "bssid": "02:00:00:aa:00:01", "essid": "Neighbor-5", "band": "5", "channel": 44, "bw": 80, "center_mhz": 5210, "signal": -71, "age": 1, "is_ubnt": false },
            { "bssid": "02:00:00:aa:00:02", "essid": "Other-5", "band": "5", "channel": 149, "bw": 40, "signal": -60, "age": 30, "is_ubnt": true }
          ],
          "spectrum_table": [
            { "channel": 36, "center_mhz": 5180, "width": 20, "utilization": 12, "interference": -64, "other_bss_count": 2 },
            { "channel": 44, "center_mhz": 5220, "width": 20, "utilization": 31, "interference": -58, "other_bss_count": 4 }
          ]
        },
        {
          "name": "wifi3", "scan_radio": true,
          "scan_table": [
            { "bssid": "02:00:00:aa:00:03", "essid": "Wide-6", "band": "6", "channel": 37, "bw": 160, "signal": -66, "age": 5 }
          ],
          "spectrum_table": [
            { "channel": 1, "center_mhz": 2412, "width": 20, "utilization": 44, "interference": -70, "other_bss_count": 9 }
          ]
        }
      ]
    }
    """;

    private static ApAgentScanPayload Payload() => JsonSerializer.Deserialize<ApAgentScanPayload>(ScanJson, Options)!;

    private static ChannelScanResult Result(string apMac, RadioBand band, params NeighborNetwork[] neighbors) => new()
    {
        ApMac = apMac, ApName = "AP", Band = band, ScanTime = Now, Neighbors = neighbors.ToList()
    };

    [Fact]
    public void The_payload_parses()
    {
        var payload = Payload();

        payload.Radios.Should().HaveCount(2);
        payload.Radios[0].Scan[0].Bssid.Should().Be("02:00:00:aa:00:01");
        payload.Radios[0].Spectrum[1].Interference.Should().Be(-58);
        payload.Radios[1].ScanRadio.Should().BeTrue();
    }

    [Fact]
    public void A_bssid_both_sources_report_keeps_the_stronger_signal_and_the_fresher_sighting()
    {
        var console = new NeighborNetwork { Bssid = "02:00:00:AA:00:01", Ssid = "Neighbor-5", Channel = 44, Signal = -75, LastSeen = Now.AddMinutes(-40) };
        var result = Result(Ap, RadioBand.Band5GHz, console);

        var merged = ApAgentScanMerger.Apply([result], _ => Payload(), Now);

        merged.Should().Be(1);
        result.Neighbors.Should().HaveCount(2, "the agent's second neighbor is added, the shared one merged");
        console.Signal.Should().Be(-71);
        console.LastSeen.Should().Be(new DateTimeOffset(2026, 9, 2, 11, 59, 49, TimeSpan.Zero));
        var other = result.Neighbors.Single(n => n.Bssid == "02:00:00:aa:00:02");
        other.IsOwnNetwork.Should().BeTrue();
        other.LastSeen.Should().Be(new DateTimeOffset(2026, 9, 2, 11, 59, 20, TimeSpan.Zero));
    }

    [Fact]
    public void A_sibling_ap_the_radio_hears_is_our_own_network_not_a_neighbor()
    {
        var result = Result(Ap, RadioBand.Band5GHz);
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "02:00:00:AA:00:01" };

        ApAgentScanMerger.Apply([result], _ => Payload(), Now, own);

        result.Neighbors.Single(n => n.Bssid == "02:00:00:aa:00:01").IsOwnNetwork.Should().BeTrue();
    }

    [Fact]
    public void A_covered_bands_spectrum_replaces_the_consoles()
    {
        var result = Result(Ap, RadioBand.Band5GHz);
        result.Channels.Add(new ChannelInfo { Channel = 36, Utilization = 90 });
        result.SpectrumTableTime = Now.AddHours(-9);

        ApAgentScanMerger.Apply([result], _ => Payload(), Now);

        result.Channels.Should().HaveCount(2);
        result.Channels.Single(c => c.Channel == 44).Utilization.Should().Be(31);
        result.Channels.Single(c => c.Channel == 44).NoiseFloor.Should().Be(-58);
        result.Channels.Single(c => c.Channel == 44).NeighborCount.Should().Be(4);
        result.SpectrumTableTime.Should().Be(new DateTimeOffset(2026, 9, 2, 11, 58, 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_scan_radio_covers_a_band_with_no_serving_table()
    {
        var band24 = Result(Ap, RadioBand.Band2_4GHz);
        var band6 = Result(Ap, RadioBand.Band6GHz);

        ApAgentScanMerger.Apply([band24, band6], _ => Payload(), Now);

        band24.Channels.Should().ContainSingle(c => c.Channel == 1 && c.Utilization == 44);
        band24.SpectrumTableTime.Should().Be(new DateTimeOffset(2026, 9, 2, 11, 59, 50, TimeSpan.Zero), "no spectrum_at: the read time stands");
        band6.Neighbors.Should().ContainSingle(n => n.Bssid == "02:00:00:aa:00:03" && n.Width == 160);
        band6.Channels.Should().BeEmpty("the scan radio measured nothing on 6 GHz");
    }

    [Fact]
    public void An_uncovered_ap_and_a_stale_reading_are_left_alone()
    {
        var uncovered = Result("aa:bb:cc:dd:ee:02", RadioBand.Band5GHz);
        uncovered.Channels.Add(new ChannelInfo { Channel = 36, Utilization = 90 });
        var stale = Result(Ap, RadioBand.Band5GHz);
        stale.Channels.Add(new ChannelInfo { Channel = 36, Utilization = 90 });

        ApAgentScanMerger.Apply([uncovered], mac => mac == Ap ? Payload() : null, Now).Should().Be(0);
        ApAgentScanMerger.Apply([stale], _ => Payload(), Now.AddMinutes(10)).Should().Be(0);

        uncovered.Channels.Single().Utilization.Should().Be(90);
        stale.Channels.Single().Utilization.Should().Be(90);
        stale.Neighbors.Should().BeEmpty();
    }
}
