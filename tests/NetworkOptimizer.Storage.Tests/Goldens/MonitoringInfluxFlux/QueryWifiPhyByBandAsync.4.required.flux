
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "wifi_client" and r._field == "tx_rate_kbps")
  |> aggregateWindow(every: 300s, fn: count, createEmpty: false)

----

from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "wifi_client" and r._field == "tx_rate_kbps")
  |> aggregateWindow(every: 300s, fn: sum, createEmpty: false)
