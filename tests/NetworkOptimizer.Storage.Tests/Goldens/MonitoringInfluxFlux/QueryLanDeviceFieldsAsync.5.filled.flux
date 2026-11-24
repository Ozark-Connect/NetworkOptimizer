
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "device_health")
  |> filter(fn: (r) => r._field == "custom_oid_1" or r._field == "if_hc_in_octets")
  |> filter(fn: (r) => r._field != "uplink_parent_mac" and r._field != "poe_near_limit" and r._field !~ /_radio$/)
  |> aggregateWindow(every: 300s, fn: first, createEmpty: false, timeSrc: "_start")

----

from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "device_health")
  |> filter(fn: (r) => r._field == "custom_oid_1" or r._field == "if_hc_in_octets")
  |> filter(fn: (r) => r._field != "uplink_parent_mac" and r._field != "poe_near_limit" and r._field !~ /_radio$/)
  |> aggregateWindow(every: 300s, fn: mean, createEmpty: false, timeSrc: "_start")

----

from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "device_health")
  |> filter(fn: (r) => r._field == "custom_oid_1" or r._field == "if_hc_in_octets")
  |> window(every: 300s)
  |> last()
