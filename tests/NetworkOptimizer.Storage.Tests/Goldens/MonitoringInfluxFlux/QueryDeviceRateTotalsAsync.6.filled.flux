
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "interface_counters")
  |> filter(fn: (r) => r.device_mac == "aa:bb:cc:dd:ee:ff" or r.device_mac == "00:11:22:33:44:55")
  |> filter(fn: (r) => r._field == "rate_in_bps" or r._field == "rate_out_bps")
  |> aggregateWindow(every: 300s, fn: mean, createEmpty: false)
  |> group(columns: ["device_mac"])
  |> aggregateWindow(every: 300s, fn: sum, createEmpty: false, timeSrc: "_start")
