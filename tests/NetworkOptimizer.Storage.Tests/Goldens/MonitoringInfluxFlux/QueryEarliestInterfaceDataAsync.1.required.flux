
from(bucket: "network_monitoring")
  |> range(start: -90d)
  |> filter(fn: (r) => r._measurement == "interface_counters")
  |> filter(fn: (r) => r._field == "rate_in_bps")
  |> first()
  |> group()
  |> min(column: "_time")
