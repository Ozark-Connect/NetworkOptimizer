
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "latency")
  |> filter(fn: (r) => r.target_id == "1.1.1.1" or r.target_id == "8.8.8.8")
  |> filter(fn: (r) => r._field == "loss_percent")
  |> group()
  |> mean()
