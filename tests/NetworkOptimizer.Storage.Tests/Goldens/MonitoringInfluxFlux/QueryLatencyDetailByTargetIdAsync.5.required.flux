
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "latency")
  |> filter(fn: (r) => r.target_id == "1.1.1.1")
  |> filter(fn: (r) => r._field == "rtt_avg_ms" or r._field == "rtt_max_ms" or r._field == "jitter_ms" or r._field == "loss_percent")
  |> aggregateWindow(every: 48s, fn: mean, createEmpty: false)
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
