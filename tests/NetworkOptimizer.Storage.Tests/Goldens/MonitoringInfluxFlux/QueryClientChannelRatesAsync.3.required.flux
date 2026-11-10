import "date"
import "math"

means = from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "wifi_client")
  |> filter(fn: (r) => r._field == "tx_rate_kbps" or r._field == "tx_throughput_bps" or r._field == "rx_throughput_bps" or r._field == "signal_dbm" or r._field == "nss")
  |> aggregateWindow(every: 15m, fn: mean, createEmpty: false)

chan = from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "wifi_client")
  |> filter(fn: (r) => r._field == "channel" or r._field == "channel_width")
  |> aggregateWindow(every: 15m, fn: last, createEmpty: false)
  |> toFloat()

union(tables: [means, chan])
  |> pivot(rowKey: ["_time"], columnKey: ["_field"], valueColumn: "_value")
  |> filter(fn: (r) => exists r.tx_rate_kbps and exists r.channel and exists r.channel_width and exists r.signal_dbm)
  |> filter(fn: (r) => (exists r.tx_throughput_bps and r.tx_throughput_bps > 50000) or (exists r.rx_throughput_bps and r.rx_throughput_bps > 50000))
  |> map(fn: (r) => ({
      device_mac: r.device_mac,
      band: r.band,
      day: string(v: date.truncate(t: r._time, unit: 1d)),
      channel: int(v: r.channel),
      width: int(v: r.channel_width),
      signal_band: 5 * int(v: math.floor(x: r.signal_dbm / 5.0)),
      tx_rate_mbps: r.tx_rate_kbps / 1000.0,
      normalized: if exists r.nss and r.nss >= 0.5 then 1 else 0,
      norm_rate_mbps: if exists r.nss and r.nss >= 0.5 then (r.tx_rate_kbps / 1000.0) / (math.round(x: r.nss) * (r.channel_width / 20.0)) else 0.0
  }))
  |> group(columns: ["device_mac", "band", "channel", "width", "signal_band", "day"])
  |> reduce(fn: (r, accumulator) => ({n: accumulator.n + 1, sum: accumulator.sum + r.tx_rate_mbps, norm_n: accumulator.norm_n + r.normalized, norm_sum: accumulator.norm_sum + r.norm_rate_mbps}), identity: {n: 0, sum: 0.0, norm_n: 0, norm_sum: 0.0})
  |> map(fn: (r) => ({device_mac: r.device_mac, band: r.band, channel: r.channel, width: r.width, signal_band: r.signal_band, day: r.day, n: r.n, mean_tx_rate_mbps: r.sum / float(v: r.n), norm_n: r.norm_n, mean_norm_rate_mbps: if r.norm_n > 0 then r.norm_sum / float(v: r.norm_n) else 0.0}))
  |> group()