
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T09:58:30Z, stop: 2026-01-15T10:00:30Z)
  |> filter(fn: (r) => r._measurement == "wired_client")
  |> filter(fn: (r) => exists r.port)
  |> filter(fn: (r) => r._field == "client_mac" or r._field == "client_ip" or r._field == "client_name")
  |> filter(fn: (r) => r.device_mac == "aa:bb:cc:dd:ee:ff")
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
