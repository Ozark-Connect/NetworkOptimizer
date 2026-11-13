
from(bucket: "network_monitoring_longterm")
  |> range(start: -1h)
  |> filter(fn: (r) => r._measurement == "events")
  |> filter(fn: (r) => r.event_type == "device_reboot")
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
  |> group(columns: ["device_mac"])
  |> sort(columns: ["_time"])
  |> last(column: "_time")
