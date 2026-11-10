
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "device_health")
  |> filter(fn: (r) => r.device_mac == "aa:bb:cc:dd:ee:ff")
  |> filter(fn: (r) => r._field == "cpu_percent" or r._field == "memory_used_percent" or r._field == "temperature_c" or r._field == "uptime_seconds" or r._field == "fan_speed_rpm")
