
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "latency" and r.target_type == "fabric" and r._field == "loss_percent")
  |> filter(fn: (r) => r._value > 0.0)
