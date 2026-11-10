
from(bucket: "network_monitoring_longterm")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "ont")
  
  |> filter(fn: (r) => r._field == "rx_power_dbm" or r._field == "tx_power_dbm" or r._field == "temperature_c" or r._field == "voltage_v" or r._field == "bias_ma" or r._field == "fec_errors" or r._field == "bip_errors" or r._field == "pon_link_status" or r._field == "link_uptime_seconds")
  |> aggregateWindow(every: 48s, fn: last, createEmpty: false)
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
