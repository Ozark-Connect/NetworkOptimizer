
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "interface_counters" and (r._field == "errors_in" or r._field == "ucast_pkts_in"))
  |> aggregateWindow(every: 1h, fn: max, createEmpty: false, timeSrc: "_start")

----

from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "interface_counters" and (r._field == "errors_in" or r._field == "ucast_pkts_in"))
  |> aggregateWindow(every: 1h, fn: min, createEmpty: false, timeSrc: "_start")
