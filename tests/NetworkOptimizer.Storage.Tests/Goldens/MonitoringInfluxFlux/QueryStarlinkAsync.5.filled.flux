
gauges = from(bucket: "network_monitoring_longterm")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "starlink")
  |> filter(fn: (r) => r.starlink_id == "starlinkId-1")
  |> filter(fn: (r) => r._field == "power_in_avg_w" or r._field == "ping_drop_rate_avg" or r._field == "fraction_obstructed" or r._field == "eth_speed_mbps" or r._field == "uptime_s" or r._field == "gps_sats" or r._field == "alignment_offset_deg" or r._field == "alert_count")
  |> aggregateWindow(every: 300s, fn: last, createEmpty: false)

peaks = from(bucket: "network_monitoring_longterm")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "starlink")
  |> filter(fn: (r) => r.starlink_id == "starlinkId-1")
  |> filter(fn: (r) => r._field == "power_in_max_w" or r._field == "ping_drop_rate_max")
  |> aggregateWindow(every: 300s, fn: max, createEmpty: false)

deltas = from(bucket: "network_monitoring_longterm")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "starlink")
  |> filter(fn: (r) => r.starlink_id == "starlinkId-1")
  |> filter(fn: (r) => r._field == "outage_count_delta" or r._field == "outage_seconds_delta")
  |> aggregateWindow(every: 300s, fn: sum, createEmpty: false)

union(tables: [gauges, peaks, deltas])
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
