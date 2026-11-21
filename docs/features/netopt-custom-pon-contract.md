# Network Optimizer Custom PON Stats - JSON Contract v1

The **Network Optimizer Custom (HTTP JSON)** ONT provider polls a plain HTTP
endpoint that returns PON-layer statistics as JSON. The contract is
vendor-neutral (field semantics follow ITU-T G.984.x / G.9807.1 terminology), so
anyone can implement it for their ONT hardware - a GPON/XGS-PON SFP stick, a
full ONT, or a small relay script on the gateway that gathers stats from the
device and serves them.

Configure it under Settings - ONT Device Monitoring with provider
"Network Optimizer Custom (HTTP JSON)". Two modes:

- **Standalone**: polled like any other ONT provider; PON state and FEC/BIP
  counters flow to the ONT Stats tab and ONT alerts.
- **Attached to a monitored SFP module** (the "Attach to SFP Module" setting):
  polled on the gateway SFP collection cycle instead, with the full metric set
  merged into that module's `sfp` measurement and charted on the SFP Stats tab.
  Use this when the ONT *is* the SFP module in your gateway, so its DDM optics
  readings and its PON internals form one time series. The gateway reads DDM off
  the SFP slot directly; the contract's optional `optics` section only fills DDM
  the gateway can't read (see [Optics precedence](#optics-precedence)).

## Endpoint semantics

- `GET http://<host>:<port>/` (any path is ignored by the reference
  implementation; the provider requests `/`). Default port: **10012**.
- Response: `Content-Type: application/json`, UTF-8, a single JSON object.
- No authentication. Serve it on a management/LAN interface only.
- The provider allows up to **15 seconds** per request and never issues
  concurrent requests, so an implementation may gather stats on demand (e.g. an
  SSH round-trip into an SFP stick) and may be a one-shot listener loop.
- Stats should be gathered fresh per request (or cached only briefly).

### Failure shape

When the implementation cannot reach the underlying device, return HTTP 200
with an error object instead of stats:

```json
{
  "error": "sfp_unreachable",
  "message": "Could not reach SFP at 169.254.1.1:30007"
}
```

A non-empty `error` marks the poll failed; `message` is a human-readable
detail shown in the UI. Any transport failure (refused, timeout, non-JSON) is
treated the same way.

## Payload

Every section and every field is **optional** - serve what the hardware
exposes; absent values are simply not recorded. All counters are **cumulative
since ONT boot** (Network Optimizer derives per-interval deltas and uses
`sfp_uptime_s` to recognize counter resets). Numbers may be JSON numbers or
numeric strings; 64-bit counters are expected.

```json
{
  "optics": {               // DDM optics, same units as SFP DDM (fallback - see note below)
    "rx_power_dbm": -18.4,  // receive optical power, dBm
    "tx_power_dbm": 2.1,    // transmit optical power, dBm
    "temperature_c": 47.3,  // transceiver temperature, degrees C
    "voltage_v": 3.28,      // supply voltage, volts
    "bias_ma": 15.15,       // laser bias current, mA (the ONT's own reading)
    "bias_warn_high_ma": 90,          // the module's own bias high-warning limit, mA
    "alarms": []                     // raised SFF-8472 flags as "kind:condition", e.g. "warning:rx_power_low"; [] when none
  },
  "lan": {
    "mode": 15,             // host-side PHY mode (raw device enum; recorded - see below)
    "link_status": 5,       // host (module-to-gateway) link state, raw enum
    "phy_duplex": 1,        // raw enum, informational
    "link_up": 1            // 1 while the host port has a link, 0 when not (for the Connection Check)
  },
  "lan_counters": {         // host-link MAC counters (module <-> gateway)
    "tx_frames": 671630919,
    "rx_frames": 850075595,
    "tx_drop_events": 0,
    "rx_fcs_err": 0,        // FCS/checksum errors - host-link signal integrity
    "buffer_overflow": 0
  },
  "ploam": {
    "curr_state": 5,        // ITU-T activation state, numeric: 1-7 = O1-O7 (5 = O5 Operation)
    "previous_state": 4,    // state before the last transition
    "elapsed_msec": 12345   // ms in the current state (uint32 on many devices; wraps ~49.7 days)
  },
  "ploam_counters": {       // downstream PLOAM messages received since boot
    "deactivations": 0,     // Deactivate_ONU-ID plus Disable_Serial_Number
    "request_key": 1,       // Request_Key
    "key_switch": 3,        // Key_Switching_Time
    "ranging_time": 3       // Ranging_Time
  },
  "link_history": {         // the ONT's own clock since boot, so a drop between two polls counts
    "o5_seconds": 1460.224, // seconds in O5
    "not_o5_seconds": 2.048, // seconds in any other state
    "o5_exits": 0,          // times the ONT left O5
    "los_events": 0, "los_seconds": 0,   // loss-of-signal alarms raised, and how long they stood
    "lof_events": 1, "lof_seconds": 0.3  // loss-of-frame alarms, likewise
  },
  "gtc_status": {
    "ds_state": 3,          // downstream GTC sync state (raw enum; 3 = sync)
    "onu_id": 49,           // ONU ID assigned by the OLT; changes on re-ranging
    "ds_fec_enable": 0,     // OLT profile: downstream FEC on/off (0/1)
    "us_fec_enable": 0,     // OLT profile: upstream FEC on/off (0/1)
    "onu_response_time": 34992, // raw ranging response time / equalization delay
    "gem_ports": 3,         // GEM Port-IDs provisioned in the GTC Port-ID table, including the OMCI port
    "eqd_bits": 283549      // equalization delay from the last Ranging_Time, upstream bit times
  },
  "gtc_counters": {
    "bip": 0,                     // BIP parity errors (0 on a healthy link)
    "hec_error_corr": 0,          // GTC header errors, corrected
    "hec_error_uncorr": 1,        // GTC header errors, uncorrectable
    "bwmap_error_corr": 0,        // upstream bandwidth-map errors, corrected
    "bwmap_error_uncorr": 0,      // upstream bandwidth-map errors, uncorrectable
    "fec_error_corr": 0,          // corrected FEC bit errors (informational)
    "fec_words_corr": 0,          // corrected FEC codewords (early warning)
    "fec_words_uncorr": 0,        // UNCORRECTABLE FEC codewords - the data-loss signal
    "fec_words_total": 0,
    "fec_seconds": 0,
    "tx_gem_frames_total": 848555332,       // (X)GEM frames sent upstream
    "tx_gem_bytes_total": 1774937051,       // (often 32-bit; unreliable at line rate)
    "tx_gem_idle_frames_total": 1076427353, // idle frames = granted-but-unused upstream capacity
    "rx_gem_frames_total": 682156109,
    "rx_gem_bytes_total": 0,
    "rx_gem_frames_dropped": 1,
    "omci_drop": 0,
    "drop": 1,
    "rx_oversized_frames": 0,
    "allocations_total": 1629046208,        // upstream grants received from the OLT
    "allocations_lost": 20,                 // missed grants - scheduling/resync indicator
    "rx_gem_idle_frames_total": 7782349680, // idle GEM frames received downstream (the whole PON's unused capacity)
    "ds_superframe": 1223280,             // downstream superframe counter (one per 125 us frame, 30 bits on GPON), read with these counters
    "us_bursts_total": 11685832,          // upstream bursts sent in the OLT's grants
    "us_dbru_total": 11631693             // upstream DBRu queue reports sent
  },
  "gpe_pon": {              // PON-side bridge port counters (packet engine)
    "ibp_good": 670798566,  "ibp_discard": 0,   // ingress good/discarded
    "ebp_good": 848843235,  "ebp_discard": 0,   // egress good/discarded
    "learning_discard": 0                       // MAC-learning-limit discards
  },
  "gpe_lan": {              // host-side bridge port, same fields as gpe_pon
    "ibp_good": 848843244,  "ibp_discard": 0,
    "ebp_good": 670798569,  "ebp_discard": 0,
    "learning_discard": 0
  },
  "encryption": {           // downstream AES state, read fresh on every poll
    "aes_active": false,        // true when any GEM port has its downstream AES decrypt bit set
    "gponmgr_enc": 0,           // enc: value of `dcli gponmgr show encryption` (observed on Calix EXOS; null where absent)
    "gem_ports_encrypted": 0    // GEM ports with the decrypt bit set
  },
  "omci": {                 // the OLT's management of the ONT
    "up": 1,                // 1 when the ONT's OMCI stack answered
    "services": 3,          // services (bridge connections) the OLT provisioned
    "rx_frames": 663,       // downstream OMCI messages received
    "mib_data_sync": 160,   // OMCI MIB data sync counter (one byte, wraps)
    "pir_bytes_per_second": 167772125, // highest PIR among the OLT's traffic descriptors
    "cir_bytes_per_second": 167772125  // highest CIR among them
  },
  "sfp_uptime_s": 358825,   // seconds since the ONT module booted (counter-reset anchor)
  "sfp_fw_version": "V1.0-220923", // the ONT module's firmware version, shown as-is
  "boot_count": 6,          // boots since the last power cycle, this one included
  "last_reset_reason": "reboot", // why the previous boot ended, in the device's own word
  "cpu": { "busy_s": 978.27, "total_s": 40509.22 }, // the ONT's CPU time since boot: non-idle, and all modes
  "olt_vendor_id": "CALX",  // OLT vendor ID (4 letters, as the ONT reports it); shown decoded, e.g. "Calix (CALX)"
  "olt_model": "E7",        // OLT model (OMCI OLT-G equipment ID); shown with the vendor, e.g. "Calix E7 (CALX)"
  "olt_version": "1.2.3",   // OLT software version (OMCI OLT-G version), shown as-is
  "pon_id": {               // the OLT's PON-ID PLOAM message (G.984.3 Annex C)
    "launch_power_dbm": 3.4, // mean launch power the OLT (or a reach extender) announces, dBm
    "odn_class": 2,         // 0 A, 1 B, 2 B+, 3 C, 4 C+
    "reach_extender": 0     // 1 when the announced power is a reach extender's; also "age_s", seconds since the last PON-ID message
  }
}
```

The PON insight fields in this example (`optics.bias_ma`, `bias_warn_high_ma`, `alarms`; `lan.link_up`; `ploam_counters`; `link_history`; `gtc_status.eqd_bits`; the idle, superframe, burst, and DBRu counters in `gtc_counters`; `omci`; `boot_count`; `last_reset_reason`; `cpu`; `pon_id`) show values from an odi-oss GPON stick's exporter, taken from the test data of #1383: live scrapes, except `eqd_bits` and `cpu`, which are from that PR's test fixture. They are a different device and moment from the other fields.

## What Network Optimizer records

Not every field is stored. The curated set, and how the standard concepts map
onto Network Optimizer's existing schema:

| Contract field | Stored as | Notes |
|---|---|---|
| `optics.rx_power_dbm` / `tx_power_dbm` | `rx_power_dbm` / `tx_power_dbm` | DDM fallback; gateway SFP DDM wins (see below) |
| `optics.temperature_c` / `voltage_v` | `temperature_c` / `voltage_v` | DDM fallback; gateway SFP DDM wins (see below) |
| `ploam.curr_state` | `pon_link_status` | same encoding as the ont measurement: `initial`, `standby`, `serial_number`, `ranging`, `operation`, `popup`, `emergency_stop` |
| `ploam.previous_state` | `pon_link_status_prev` | same encoding |
| `ploam.elapsed_msec` | `ploam_elapsed_ms` | |
| `gtc_status.ds_state` | `gtc_ds_state` | |
| `gtc_status.onu_id` | `onu_id` | |
| `gtc_status.ds_fec_enable` / `us_fec_enable` | `ds_fec_enabled` / `us_fec_enabled` | |
| `gtc_status.onu_response_time` | `onu_response_time` | |
| `gtc_status.gem_ports` | `gem_ports` | includes the OMCI port |
| `encryption.aes_active` | `aes_active` | boolean; drives the AES alerts (see below) |
| `encryption.gem_ports_encrypted` | `gem_ports_encrypted` | |
| `encryption.gponmgr_enc` | `gponmgr_enc` | recorded as-is. Other ONT firmware also ships a `gponmgr`, but this command and its `enc:` output have been observed only on Calix EXOS |
| `gtc_counters.bip` | `bip_errors` | standard field |
| `gtc_counters.fec_words_uncorr` | `fec_errors` | standard field: uncorrectable codewords |
| `gtc_counters.fec_words_corr` | `fec_corrected_words` | |
| `gtc_counters.hec_error_corr` / `uncorr` | `hec_corrected` / `hec_uncorrected` | |
| `gtc_counters.bwmap_error_corr` / `uncorr` | `bwmap_corrected` / `bwmap_uncorrected` | |
| `gtc_counters.tx_gem_frames_total` etc. | `gem_tx_frames`, `gem_tx_idle_frames`, `gem_rx_frames`, `gem_rx_dropped` | |
| `gtc_counters.allocations_total` / `lost` | `alloc_total` / `alloc_lost` | |
| `gpe_pon` / `gpe_lan` discards | `gpe_{pon,lan}_{ingress,egress,learning}_discard` | good-frame counters are not stored |
| `lan.link_status` | `lan_link_status` | |
| `lan.mode` | `lan_mode` | raw enum, recorded for change detection - see below |
| `lan_counters.*` | `lan_tx_frames`, `lan_rx_frames`, `lan_tx_drop_events`, `lan_rx_fcs_err`, `lan_buffer_overflow` | |
| `sfp_uptime_s` | `sfp_uptime_s` | |
| `olt_vendor_id` | `olt_vendor_id` | raw ID; decoded for display only. Standalone, also the `ont` measurement's `olt_vendor` tag |
| `sfp_fw_version` | `firmware_version` | string, recorded as-is |
| `olt_model` | `olt_equipment_id` | string. Standalone, also the `ont` measurement's `olt_model` tag (the reason for the field's different name) |
| `olt_version` | `olt_version` | string, recorded as-is |

| `gtc_counters.fec_error_corr` | `fec_corrected_bits` | with the frame count, the pre-FEC bit error ratio |
| `gtc_counters.rx_gem_idle_frames_total` / `ds_superframe` | `gem_rx_idle_frames` / `ds_superframe` | the whole-PON downstream load (see below) |
| `gtc_counters.us_bursts_total` / `us_dbru_total` | `us_bursts` / `us_dbru` | |
| `gtc_status.eqd_bits` | `eqd_bits` | the delay from the last Ranging_Time message, in upstream bit times; `onu_response_time` stays the device's raw value, so serve both when you have both |
| `optics.bias_ma` / `bias_warn_high_ma` | `ont_bias_ma` / `bias_warn_high_ma` | the ONT's own reading, kept apart from the gateway's DDM |
| `optics.alarms` | `transceiver_alarms`, `transceiver_alarm_count` | |
| `lan.link_up` | `host_link_up` | |
| `pon_id.*` | `olt_launch_power_dbm`, `odn_class`, `reach_extender`, `pon_id_age_s` | with `optics.rx_power_dbm`, also `path_loss_db` |
| `link_history.*` | `o5_seconds`, `not_o5_seconds`, `o5_exits`, `los_events`, `los_seconds`, `lof_events`, `lof_seconds` | |
| `ploam_counters.*` | `olt_deactivations`, `ploam_request_key`, `ploam_key_switch`, `ploam_ranging_time` | |
| `omci.*` | `omci_up`, `omci_services`, `omci_rx_frames`, `mib_data_sync`, `td_pir_bytes_s`, `td_cir_bytes_s` | |
| `cpu.busy_s` / `total_s` | `cpu_busy_s` / `cpu_total_s` | |
| `boot_count` / `last_reset_reason` | `boot_count` / `last_reset_reason` | |

Not recorded: `lan.phy_duplex` (static config), GEM byte counters
(32-bit wrap), `drop` / `omci_drop` / `rx_oversized_frames`,
`fec_words_total` / `fec_seconds`, and the `gpe_*` good-frame
counters (redundant with `lan_counters` and GEM frame counters).

### What each card needs

Every field stays optional. A card shows once its fields arrive, and stays hidden otherwise.

| Card or check | Fields |
|---|---|
| **PON Downstream Load**, **PON Load by Hour** | `gtc_counters.rx_gem_idle_frames_total` and `ds_superframe`, from the same sample; `gtc_status.ds_fec_enable` sets the payload |
| **Path Loss**, and the two path loss alerts | `pon_id.launch_power_dbm`, `optics.rx_power_dbm`; `pon_id.odn_class` for the class limit |
| **Link Downtime** | `link_history.*`; `ploam_counters.deactivations` names an OLT-side cause |
| **Laser Bias Current** | `optics.bias_ma` (`bias_warn_high_ma`, `alarms` add the limit and the module's flags) |
| **Upstream Grants** | `gtc_counters.allocations_total`, `us_bursts_total`, `us_dbru_total` |
| **ONT CPU Load** | `cpu.busy_s`, `cpu.total_s` |
| **Provider Activity** | `omci.*`, `ploam_counters.*`, `gtc_status.eqd_bits` |
| **Connection Check** | `ploam.curr_state` at least; each further check reads the fields above it can find |

The whole-PON load works because the OLT fills unused downstream capacity with idle GEM frames
that every ONT on the PON receives. It is read against the superframe counter rather than the
clock, so the counter must come from the same read as the idle count. The PON's load is the whole
splitter tree's, so the "This ONT" line beside it needs per-port 64-bit byte counters, which this
contract does not carry.

### Host link fields

`lan.link_status` and `lan.mode` are raw device enums, so Network Optimizer stores
the numbers without interpreting them and displays them as-is. They earn their
place because a module can drop or renegotiate its host link while the PON side
stays perfectly healthy - PLOAM holds at O5, `gtc_status.ds_state` holds at sync,
`sfp_uptime_s` keeps climbing, and every GTC counter keeps accumulating, because
the OLT never stopped sending. A change in these two is the only thing in the
payload that marks the event. `lan.mode` matters most on recovery: it says which
rate the link came back at.

Note that the poll itself usually rides the same host link, so an implementation
will more often report the outage as a failed poll (see [Failure shape](#failure-shape))
than as a `link_status` of down. `sfp_uptime_s` continuing across the gap is what
separates a host-link drop from a module reboot.

### Encryption

`encryption.aes_active` is the AES flag. A `null` (or absent) field means the read
failed or the implementation does not serve it; Network Optimizer stores nothing
for it on that poll, never `false`. Two ONT alerts run off transitions of this flag:

- **ONT: AES Encryption Enabled** (`ont.encryption_enabled`, Warning): `false` to `true`.
- **ONT: AES Encryption Disabled** (`ont.encryption_disabled`, Error): `true` to `false`.

The first reading after a Network Optimizer restart only sets the baseline. A
reading is skipped (it neither alerts nor moves the baseline) while PLOAM is not
in O5, or when `aes_active` is `false` and `gtc_status.gem_ports` is 1 or less:
a re-ranging ONT has no data GEM ports and so no decrypt bits, which is not the
OLT turning encryption off.

### Duplicated counters

Some hardware serves two contract fields off one register. On Lantiq-based
modules `rx_gem_frames_dropped` and `hec_error_uncorr` are both the GEM header
HEC error counter, so they are always equal - by construction, not coincidence,
and regardless of whether the OLT profile has FEC enabled. Both are still stored,
since another implementation may count them separately, but the SFP Stats error
chart drops the GEM series when it matches HEC point for point rather than
drawing one line on top of another.

### Optics precedence

The `optics` section is optional and exists for modules whose DDM the gateway
cannot read off its SFP slot - many GPON sticks present no usable DDM there. When
the config is **attached to a monitored SFP module**, the gateway's own DDM
reading wins field by field: `optics.rx_power_dbm` is written only when the
gateway slot reports no RX power, and likewise for TX power, temperature, and
voltage. So supplying `optics` never overrides what the gateway can already see;
it just fills the gaps. In **standalone** mode there is no gateway DDM to defer
to, so the `optics` values flow straight to the ONT Stats tab.

`optics.bias_ma`, `bias_warn_high_ma`, and `alarms` are not fallbacks: they are the ONT's own
readings, stored apart from the gateway's DDM bias (`ont_bias_ma`, not `bias_ma`) and written
whenever served.

DDM alerts continue to run off the gateway's SFP DDM readings only; the
contract-supplied fallback is charted and displayed but is not yet wired into DDM
alert thresholds.

## Reference implementation

The first serving-side implementation is a pair of shell scripts on a UniFi
gateway that SSH into a Lantiq-based GPON SFP stick, run the vendor `onu` CLI
(`ploamsg`, `gtcsg`, `gtctcg`, `gpebptcg`, `lanpsg`, `lantcg`), and serve the
JSON via a one-shot netcat accept loop on port 10012. Any equivalent works -
the provider only sees the HTTP contract above.
