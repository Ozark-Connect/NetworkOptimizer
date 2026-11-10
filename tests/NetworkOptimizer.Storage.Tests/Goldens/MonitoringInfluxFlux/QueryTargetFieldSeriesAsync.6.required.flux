
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "latency" and (r._field == "custom_oid_1"))
  |> filter(fn: (r) => r.target_id == "1.1.1.1")
  |> aggregateWindow(every: 300s, fn: mean, createEmpty: false, timeSrc: "_start")
