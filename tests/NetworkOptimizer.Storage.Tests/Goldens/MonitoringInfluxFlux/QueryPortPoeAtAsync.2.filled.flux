
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T09:55:00Z, stop: 2026-01-15T10:00:30Z)
  |> filter(fn: (r) => r._measurement == "interface_counters" and (r._field == "poe_power" or r._field == "poe_mode"))
