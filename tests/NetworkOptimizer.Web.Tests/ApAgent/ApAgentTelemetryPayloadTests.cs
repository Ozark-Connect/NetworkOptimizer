using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The wire shape. The agent serves snake_case and the counters the additive fields come from sit
/// on the link rather than the client, so a silently unmapped name would read as an absent counter
/// instead of a bug.
/// </summary>
public class ApAgentTelemetryPayloadTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private const string ClientsJson = """
    {
      "ap": { "hostname": "ap-test", "mac": "aa:bb:cc:dd:ee:ff" },
      "count": 1,
      "collected_at": "2026-08-24T12:00:00Z",
      "sources": {
        "events": { "available": true, "last_collected_at": "2026-08-24T11:59:58Z" },
        "fast": { "available": true, "last_collected_at": "2026-08-24T11:59:59Z" },
        "slow": { "available": true, "last_collected_at": "2026-08-24T11:59:30Z" }
      },
      "clients": [
        {
          "key": "00:11:22:33:44:60",
          "mac": "00:11:22:33:44:60",
          "mld_mac": "00:11:22:33:44:60",
          "is_mlo": true,
          "link_count": 2,
          "band": "6",
          "channel": 37,
          "bw": 160,
          "signal": -61,
          "noise": -96,
          "snr": 35,
          "tx_rate_kbps": 1441000,
          "rx_rate_kbps": 960000,
          "satisfaction": 97,
          "capabilities": { "is_11be": true, "is_mlo": true, "nss": 4, "bw_max_supp": 320 },
          "links": [
            {
              "mac": "00:11:22:33:44:55",
              "vap": "ath0",
              "band": "5",
              "active": false,
              "nss": 2,
              "ccq": 300,
              "tx_bytes": 10,
              "rx_bytes": 20,
              "tx_retries": 1
            },
            {
              "mac": "00:11:22:33:44:56",
              "vap": "ath1",
              "band": "6",
              "active": true,
              "nss": 2,
              "ccq": 942,
              "tx_bytes": 987654321,
              "rx_bytes": 123456789,
              "tx_retries": 8123,
              "wifi_tx_attempts": 991234,
              "wifi_tx_dropped": 17,
              "wifi_tx_latency_mov": { "avg": 4500, "max": 21000, "min": 900 },
              "tx_tcp_stats": { "lat_avg": 11, "lat_max": 88, "stalls": 4, "retries": 9 }
            }
          ]
        }
      ]
    }
    """;

    private const string RadiosJson = """
    {
      "collected_at": "2026-08-24T12:00:00Z",
      "count": 1,
      "radios": [
        {
          "name": "wifi0",
          "radio": "na",
          "band": "5",
          "channel": 44,
          "bw": 160,
          "center_mhz": 5250,
          "noise_floor": -96,
          "counters": { "cu_total": 41, "cu_interf": 12, "pdev_resets": 74144, "tx_data_bytes": 9 },
          "counter_deltas": { "cu_total": 3, "cycle_cnt": 300 },
          "delta_seconds": 30.5
        }
      ]
    }
    """;

    [Fact]
    public void Clients_payload_parses_into_one_client_on_its_mld_mac()
    {
        var payload = JsonSerializer.Deserialize<ApAgentClientsPayload>(ClientsJson, Options);

        payload.Should().NotBeNull();
        payload!.Clients.Should().HaveCount(1);
        payload.Sources!.Slow!.Available.Should().BeTrue();

        var client = payload.Clients[0];
        client.Key.Should().Be("00:11:22:33:44:60");
        client.MldMac.Should().Be("00:11:22:33:44:60");
        client.IsMlo.Should().BeTrue();
        client.Band.Should().Be("6");
        client.Links.Should().HaveCount(2);
        client.Capabilities!.Nss.Should().Be(4);
    }

    [Fact]
    public void Active_link_counters_reach_the_additive_fields()
    {
        var payload = JsonSerializer.Deserialize<ApAgentClientsPayload>(ClientsJson, Options)!;
        var sample = ApAgentWifiFieldMapper.ToSample(payload.Clients[0], "aa:bb:cc:dd:ee:ff");

        sample.Should().NotBeNull();
        sample!.ClientMac.Should().Be("00:11:22:33:44:60");
        sample.Band.Should().Be("6ghz");
        sample.TxRetries.Should().Be(8123);
        sample.TxAttempts.Should().Be(991234);
        sample.TxDropped.Should().Be(17);
        sample.Ccq.Should().Be(942);
        sample.Nss.Should().Be(2);
        sample.TcpStalls.Should().Be(4);
        sample.TcpLatAvgMs.Should().Be(11);
        sample.LatencyAvgMs.Should().Be(4.5);
        sample.LatencyMaxMs.Should().Be(21.0);
        sample.TxBytes.Should().Be(987654321);
        sample.SignalDbm.Should().Be(-61);
        sample.Satisfaction.Should().Be(97);
    }

    [Fact]
    public void Radios_payload_parses_its_counter_maps()
    {
        var payload = JsonSerializer.Deserialize<ApAgentRadiosPayload>(RadiosJson, Options);

        payload.Should().NotBeNull();
        payload!.Radios.Should().HaveCount(1);

        var radio = payload.Radios[0];
        radio.Name.Should().Be("wifi0");
        radio.Counters!["cu_total"].Should().Be(41);
        radio.Counters["pdev_resets"].Should().Be(74144);
        radio.Deltas!["cycle_cnt"].Should().Be(300);
        radio.DeltaSeconds.Should().Be(30.5);
        radio.CenterMhz.Should().Be(5250);
    }

    [Fact]
    public void A_channel_change_event_parses_with_both_sides()
    {
        const string json = """
        {
          "agent_started_at": "2026-09-02T17:00:00Z",
          "collected_at": "2026-09-02T17:20:10Z",
          "events": [
            {
              "seq": 7, "type": "channel_change", "radio": "wifi2", "collected_at": "2026-09-02T17:20:05Z",
              "channel": { "band": "6", "from_channel": 101, "from_bw": 160, "from_center_mhz": 6505, "to_channel": 69, "to_bw": 160, "to_center_mhz": 6345 }
            }
          ]
        }
        """;

        var payload = JsonSerializer.Deserialize<ApAgentEventsPayload>(json, Options)!;

        var e = payload.Events.Single();
        e.Type.Should().Be(ApAgentEventTypes.ChannelChange);
        e.Radio.Should().Be("wifi2");
        e.Channel.Should().NotBeNull();
        e.Channel!.FromChannel.Should().Be(101);
        e.Channel.ToChannel.Should().Be(69);
        e.Channel.ToCenterMhz.Should().Be(6345);
        e.Channel.Band.Should().Be("6");
    }

    [Fact]
    public void A_radio_without_a_center_reads_as_absent_not_zero()
    {
        var payload = JsonSerializer.Deserialize<ApAgentRadiosPayload>(
            RadiosJson.Replace("\"center_mhz\": 5250,", ""), Options)!;

        payload.Radios[0].CenterMhz.Should().BeNull();
    }
}
