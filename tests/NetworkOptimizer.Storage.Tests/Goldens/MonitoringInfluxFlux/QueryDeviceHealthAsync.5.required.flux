
from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "device_health")
  |> filter(fn: (r) => r.device_mac == "aa:bb:cc:dd:ee:ff")
  |> filter(fn: (r) => r._field == "cpu_percent" or r._field == "memory_used_percent" or r._field == "temperature_c" or r._field == "uptime_seconds" or r._field == "fan_speed_rpm")
  |> map(fn: (r) => ({ r with _value: if r._field == "temperature_c" and float(v: r._value) > 200.0 then float(v: r._value) / 1000.0 else float(v: r._value) }))
  |> aggregateWindow(every: 48s, fn: mean, createEmpty: false)
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
