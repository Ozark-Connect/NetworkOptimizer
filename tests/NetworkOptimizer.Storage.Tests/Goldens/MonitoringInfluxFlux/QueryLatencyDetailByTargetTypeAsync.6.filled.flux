
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "latency")
  |> filter(fn: (r) => r.target_type == "fabric")
  |> filter(fn: (r) => not exists r.wan or r.wan == "wan1" or r.wan == "wan2")
  |> filter(fn: (r) => r._field == "rtt_avg_ms" or r._field == "rtt_max_ms" or r._field == "jitter_ms" or r._field == "loss_percent")
  |> aggregateWindow(every: 300s, fn: mean, createEmpty: false)
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
