
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "device_health")
  |> filter(fn: (r) => r.device_mac == "aa:bb:cc:dd:ee:ff")
  |> filter(fn: (r) => r._field == "cpu_percent")
  |> aggregateWindow(every: 300s, fn: mean, createEmpty: false)
