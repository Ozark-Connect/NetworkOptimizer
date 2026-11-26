
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T09:00:00Z, stop: 2026-01-15T10:00:00Z)
  |> filter(fn: (r) => r._measurement == "wired_client" and r._field == "client_mac")
  |> filter(fn: (r) => r.device_mac == "aa:bb:cc:dd:ee:ff" or r.device_mac == "aa:bb:cc:dd:ee:01")
  |> filter(fn: (r) => r.port == "3" or r.port == "7")
  |> last()
