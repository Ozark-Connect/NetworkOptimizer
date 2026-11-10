
from(bucket: "network_monitoring_longterm")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "events")
  |> filter(fn: (r) => r.event_type == "device_reboot")
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
