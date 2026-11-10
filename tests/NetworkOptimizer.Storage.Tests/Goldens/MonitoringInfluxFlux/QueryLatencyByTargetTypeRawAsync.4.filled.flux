
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "latency")
  |> filter(fn: (r) => r.target_type == "fabric")
  |> filter(fn: (r) => r._field == "rtt_avg_ms" or r._field == "loss_percent")
