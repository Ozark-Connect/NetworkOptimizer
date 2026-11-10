base = from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "interface_counters" and r.device_mac == "aa:bb:cc:dd:ee:ff")
  |> filter(fn: (r) => r.if_name == "eth0.10")
  |> filter(fn: (r) => r._field == "rate_out_bps" or r._field == "rate_in_bps")
speed = from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "interface_counters" and r.device_mac == "aa:bb:cc:dd:ee:ff")
  |> filter(fn: (r) => r.if_name == "eth0.10")
  |> filter(fn: (r) => r._field == "speed_bps")
union(tables: [
  base |> aggregateWindow(every: 300s, fn: mean, createEmpty: false, timeSrc: "_start") |> map(fn: (r) => ({r with s: if r._field == "rate_out_bps" then "tx_mean" else "rx_mean"})),
  base |> aggregateWindow(every: 300s, fn: max, createEmpty: false, timeSrc: "_start") |> map(fn: (r) => ({r with s: if r._field == "rate_out_bps" then "tx_max" else "rx_max"})),
  speed |> aggregateWindow(every: 300s, fn: max, createEmpty: false, timeSrc: "_start") |> map(fn: (r) => ({r with s: "cap", _value: float(v: r._value)}))])