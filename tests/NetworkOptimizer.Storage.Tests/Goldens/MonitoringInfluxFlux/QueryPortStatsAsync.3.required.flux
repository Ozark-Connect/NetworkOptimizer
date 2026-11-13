
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T09:58:30Z, stop: 2026-01-15T10:00:30Z)
  |> filter(fn: (r) => r._measurement == "interface_counters")
  |> filter(fn: (r) => r.device_mac == "aa:bb:cc:dd:ee:ff")
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
