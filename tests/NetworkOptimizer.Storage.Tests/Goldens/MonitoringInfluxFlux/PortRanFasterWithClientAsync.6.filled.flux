
from(bucket: "network_monitoring")
  |> range(start: <now>)
  |> filter(fn: (r) => r._measurement == "interface_counters" and r.device_mac == "aa:bb:cc:dd:ee:ff" and r.port_id == "pt:7" and r._field == "link_speed_mbps")
  |> aggregateWindow(every: 1h, fn: max, createEmpty: false)
  |> filter(fn: (r) => r._value > 7)
  |> keep(columns: ["_time"])
