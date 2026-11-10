from(bucket: "network_monitoring")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-16T10:00:00Z)
  |> filter(fn: (r) => r._measurement == "wifi_client")
  |> filter(fn: (r) => r._field == "client_mac" or r._field == "channel_width" or r._field == "nss" or r._field == "channel" or r._field == "signal_dbm" or r._field == "tx_rate_kbps" or r._field == "rx_rate_kbps")
  |> pivot(rowKey: ["_time"], columnKey: ["_field"], valueColumn: "_value")
  |> filter(fn: (r) => exists r.client_mac and exists r.nss and exists r.channel_width and exists r.channel and exists r.signal_dbm and exists r.tx_rate_kbps and exists r.rx_rate_kbps)
  |> group(columns: ["client_mac", "band", "device_mac", "channel"])
  |> reduce(
       fn: (r, accumulator) => ({
         width: if r.channel_width > accumulator.width then r.channel_width else accumulator.width,
         nss: if r.nss > accumulator.nss then r.nss else accumulator.nss,
         tx: if r.tx_rate_kbps > accumulator.tx then r.tx_rate_kbps else accumulator.tx,
         rx: if r.rx_rate_kbps > accumulator.rx then r.rx_rate_kbps else accumulator.rx,
         sigmax: if accumulator.n == 0 or r.signal_dbm > accumulator.sigmax then r.signal_dbm else accumulator.sigmax,
         sigmin: if accumulator.n == 0 or r.signal_dbm < accumulator.sigmin then r.signal_dbm else accumulator.sigmin,
         n: accumulator.n + 1
       }),
       identity: {width: 0, nss: 0, tx: 0, rx: 0, sigmax: 0.0, sigmin: 0.0, n: 0}
     )
  |> group()