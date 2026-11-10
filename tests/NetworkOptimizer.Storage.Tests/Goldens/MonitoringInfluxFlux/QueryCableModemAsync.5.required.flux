
gauges = from(bucket: "network_monitoring_longterm")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "cable_modem")
  
  |> filter(fn: (r) => r._field == "ds_power_avg_dbmv" or r._field == "ds_snr_avg_db" or r._field == "us_power_avg_dbmv" or r._field == "locked_ds_channels" or r._field == "locked_us_channels")
  |> aggregateWindow(every: 48s, fn: last, createEmpty: false)

deltas = from(bucket: "network_monitoring_longterm")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "cable_modem")
  
  |> filter(fn: (r) => r._field == "correctables_delta" or r._field == "uncorrectables_delta")
  |> aggregateWindow(every: 48s, fn: sum, createEmpty: false)

union(tables: [gauges, deltas])
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
