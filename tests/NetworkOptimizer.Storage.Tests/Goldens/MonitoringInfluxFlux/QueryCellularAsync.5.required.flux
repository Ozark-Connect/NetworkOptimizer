
from(bucket: "network_monitoring_longterm")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "cellular")
  
  |> filter(fn: (r) => r._field == "rsrp" or r._field == "rsrq" or r._field == "snr" or r._field == "rssi" or r._field == "signal_quality" or r._field == "band" or r._field == "carrier" or r._field == "bandwidth_mhz" or r._field == "cell_id" or r._field == "roaming" or r._field == "software_version" or r._field == "host_version" or r._field == "module_vendor" or r._field == "module_model")
  |> aggregateWindow(every: 48s, fn: last, createEmpty: false)
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
