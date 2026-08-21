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
    "voltage_v": 3.28       // supply voltage, volts
  },
  "lan": {
    "mode": 15,             // host-side PHY mode (raw device enum; recorded - see below)
    "link_status": 5,       // host (module-to-gateway) link state, raw enum
    "phy_duplex": 1         // raw enum, informational
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
  "gtc_status": {
    "ds_state": 3,          // downstream GTC sync state (raw enum; 3 = sync)
    "onu_id": 49,           // ONU ID assigned by the OLT; changes on re-ranging
    "ds_fec_enable": 0,     // OLT profile: downstream FEC on/off (0/1)
    "us_fec_enable": 0,     // OLT profile: upstream FEC on/off (0/1)
    "onu_response_time": 34992  // raw ranging response time / equalization delay
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
    "allocations_lost": 20                  // missed grants - scheduling/resync indicator
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
  "sfp_uptime_s": 358825    // seconds since the ONT module booted (counter-reset anchor)
}
```

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

Not recorded: `lan.phy_duplex` (static config), GEM byte counters
(32-bit wrap), `drop` / `omci_drop` / `rx_oversized_frames`,
`fec_error_corr` / `fec_words_total` / `fec_seconds`, and the `gpe_*` good-frame
counters (redundant with `lan_counters` and GEM frame counters).

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

DDM alerts continue to run off the gateway's SFP DDM readings only; the
contract-supplied fallback is charted and displayed but is not yet wired into DDM
alert thresholds.

## Reference implementation

The first serving-side implementation is a pair of shell scripts on a UniFi
gateway that SSH into a Lantiq-based GPON SFP stick, run the vendor `onu` CLI
(`ploamsg`, `gtcsg`, `gtctcg`, `gpebptcg`, `lanpsg`, `lantcg`), and serve the
JSON via a one-shot netcat accept loop on port 10012. Any equivalent works -
the provider only sees the HTTP contract above.
