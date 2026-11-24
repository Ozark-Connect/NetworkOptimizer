
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "device_health" and r._field == "uplink_parent_mac")
  |> filter(fn: (r) => r._value == "aa:bb:cc:dd:ee:ff")
  |> keep(columns: ["device_mac", "_value"])
  |> distinct(column: "device_mac")
