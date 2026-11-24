
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "interface_counters")
  |> filter(fn: (r) => r._field == "custom_oid_1")
  |> aggregateWindow(every: 300s, fn: first, createEmpty: false, timeSrc: "_start")

----

from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "interface_counters")
  |> filter(fn: (r) => r._field == "custom_oid_1")
  |> aggregateWindow(every: 300s, fn: last, createEmpty: false, timeSrc: "_start")

----

from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "interface_counters")
  |> filter(fn: (r) => r._field == "custom_oid_1")
  |> aggregateWindow(every: 300s, fn: max, createEmpty: false, timeSrc: "_start")

----

from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "interface_counters")
  |> filter(fn: (r) => r._field == "custom_oid_1")
  |> aggregateWindow(every: 300s, fn: min, createEmpty: false, timeSrc: "_start")
