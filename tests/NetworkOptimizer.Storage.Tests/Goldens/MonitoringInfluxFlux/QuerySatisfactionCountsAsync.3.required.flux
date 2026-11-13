
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "wifi_client" and r._field == "satisfaction")
  |> aggregateWindow(every: 1h, fn: count, createEmpty: false, timeSrc: "_start")

----

from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "wifi_client" and r._field == "satisfaction")
  |> filter(fn: (r) => r._value < 60)
  |> aggregateWindow(every: 1h, fn: count, createEmpty: false, timeSrc: "_start")

----

from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "wifi_client" and r._field == "satisfaction")
  |> filter(fn: (r) => r._value < 80)
  |> aggregateWindow(every: 1h, fn: count, createEmpty: false, timeSrc: "_start")
