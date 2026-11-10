
from(bucket: "network_monitoring_longterm")
  |> range(start: 2026-01-15T10:00:00Z, stop: 2026-01-15T12:00:00Z)
  |> filter(fn: (r) => r._measurement == "sfp")
  |> filter(fn: (r) => (r.device_mac == "cd" and r.port_name == "3") or (r.device_mac == "deceac" and r.port_name == "AA-BB-CC-DD-EE-FF"))
  |> filter(fn: (r) => r._field == "pon_link_status" or r._field == "pon_link_status_prev" or r._field == "onu_id" or r._field == "ds_fec_enabled" or r._field == "us_fec_enabled" or r._field == "onu_response_time" or r._field == "sfp_uptime_s" or r._field == "ploam_elapsed_ms" or r._field == "bip_errors" or r._field == "fec_errors" or r._field == "fec_corrected_words" or r._field == "hec_corrected" or r._field == "hec_uncorrected" or r._field == "bwmap_corrected" or r._field == "bwmap_uncorrected" or r._field == "gem_tx_frames" or r._field == "gem_tx_idle_frames" or r._field == "gem_rx_frames" or r._field == "gem_rx_dropped" or r._field == "alloc_lost" or r._field == "lan_link_status" or r._field == "lan_mode" or r._field == "lan_rx_fcs_err" or r._field == "lan_tx_drop_events" or r._field == "lan_buffer_overflow" or r._field == "olt_vendor_id" or r._field == "registration_status" or r._field == "key_exchange_status" or r._field == "firmware_version" or r._field == "olt_equipment_id" or r._field == "olt_version")
  |> aggregateWindow(every: 300s, fn: last, createEmpty: false)
  |> pivot(rowKey:["_time"], columnKey: ["_field"], valueColumn: "_value")
