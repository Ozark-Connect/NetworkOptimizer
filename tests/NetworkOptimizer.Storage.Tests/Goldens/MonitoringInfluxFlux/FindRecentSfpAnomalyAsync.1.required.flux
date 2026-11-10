
from(bucket: "network_monitoring_longterm")
  |> range(start: <now>)
  |> filter(fn: (r) => r._measurement == "sfp")
  |> filter(fn: (r) => r._field == "temp_c" or r._field == "rx_power_dbm")
  |> filter(fn: (r) => (r._field == "temp_c" and r._value > 75.0) or (r._field == "rx_power_dbm" and r._value < -25.0))
  |> sort(columns: ["_time"], desc: true)
  |> limit(n: 1)
