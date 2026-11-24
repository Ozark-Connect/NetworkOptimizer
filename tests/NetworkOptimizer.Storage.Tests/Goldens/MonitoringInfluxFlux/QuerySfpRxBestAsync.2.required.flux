
from(bucket: "network_monitoring_longterm")
  |> range(start: 2026-01-15T10:00:00Z)
  |> filter(fn: (r) => r._measurement == "sfp" and r._field == "rx_power_dbm")
  |> max()
