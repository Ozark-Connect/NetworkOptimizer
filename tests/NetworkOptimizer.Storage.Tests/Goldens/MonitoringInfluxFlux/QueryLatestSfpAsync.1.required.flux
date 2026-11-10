
from(bucket: "network_monitoring_longterm")
  |> range(start: -6h)
  |> filter(fn: (r) => r._measurement == "sfp")
  |> last()
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
