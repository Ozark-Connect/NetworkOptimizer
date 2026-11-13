
from(bucket: "network_monitoring_longterm")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "wifi_client" and r._field == "custom_oid_1")
  |> filter(fn: (r) => r.cm_id == "3" and r.device_mac == "aa:bb:cc:dd:ee:ff")
  |> aggregateWindow(every: 300s, fn: mean, createEmpty: false)
