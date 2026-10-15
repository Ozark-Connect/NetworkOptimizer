# Native voluntary AP steering

Some AP firmwares, including UX7, expose native hostapd control sockets without
hostapd objects on ubus. Telemetry can work on these APs while the existing ubus
steering route remains unavailable. Contract 28 adds a limited native operation.

## Supported operation

An authorized, single-link, non-MLO client on a supported 5 GHz VAP can be asked
to move to another AP. The destination must supply its own neighbor report through
`/neighbors`, plus public RSN security metadata. The source checks that the SSID,
WPA mode, key-management methods and pairwise ciphers match its current network.
Unknown metadata or a changed association causes a refusal before transmission.

The request uses `disassoc_imminent=0`, `disassoc_timer=0`, `pref=1` and
`abridged=0`. It starts no departure guard, reassociation ban, forced disconnect,
or automatic retry. A client can refuse and stay connected. A successful reply
confirms that hostapd accepted the request; the event stream supplies evidence of
any actual move separately.

MLO steering, Change Band, native own-report construction and native bounce
guards are outside this operation. The existing ubus backend retains its own
request and guard behavior.

## Capability and compatibility

`/health` and `/capabilities` advertise `native_voluntary_vaps`, a list of VAPs
supporting this operation. Native probing runs only when hostapd ubus objects are
unavailable. It checks `STATUS`, public fields from `GET_CONFIG`, and recognition
of `BSS_TM_REQ` with no station address. This last probe cannot target a client:
hostapd rejects the missing MAC before looking up a station or sending a frame.
See the [upstream hostapd handler](https://android.googlesource.com/platform/external/wpa_supplicant_8/+/refs/heads/main/src/ap/ctrl_iface_ap.c).

The server requires contract 28 or newer and fresh capability evidence. It
rechecks `/health` before a steering request. Client Performance refreshes its
availability after an AP move and once a minute, and does not offer Change Band
for this backend.

The new authenticated endpoint is:

```text
POST /clients/{mac}/voluntary-bss-transitions
```

```json
{
  "candidates": [{
    "element": "02000000020107000000732409",
    "ssid": "Example",
    "security": {"wpa": "2", "key_mgmt": "WPA-PSK", "pairwise": "CCMP"}
  }]
}
```

The example uses synthetic addresses. Legacy duration and guard fields are
rejected. Older agents reject the distinct route instead of ignoring a new flag
and applying their default eviction timer. The server never falls back to the
legacy route after an error or uncertain acknowledgment.

## Request bounds and validation

- At most eight candidates; each report is 13–255 bytes with valid subelement
  lengths and a unicast BSSID. Only 5 GHz reports are accepted. Duplicates and
  this AP's own BSSIDs are rejected. The encoded Neighbor Report IE list is
  limited to hostapd's 1000-byte candidate buffer.
- Each command uses a separate, private Unix datagram socket with no event
  attachment. Replies are limited to 16 KiB, commands to 4095 bytes, and each
  exchange to 500 ms. A complete request, including waiting for another native
  request, has a six-second deadline.
- The agent checks every non-backhaul VAP. An exact `FAIL` station reply means
  absent; timeout, `UNKNOWN COMMAND` and malformed replies mean unknown and
  stop the request. The holding station must advertise BTM support.
- The agent rechecks the source band, public network configuration and station
  immediately before sending. Hostapd's own station lookup bounds the remaining
  association race. Native requests on one AP are serialized.
- `GET_CONFIG` may contain credentials. Only SSID, BSSID, WPA mode, key-management
  methods and RSN pairwise ciphers are parsed; credentials never enter response
  metadata, errors or logs.

## Validation

Unix datagram fixtures cover authenticated routing, voluntary command encoding,
capability recognition, candidate validation, association races, unknown reads,
concurrent reply isolation, cancellation and a lost acknowledgment with exactly
one transmission. Web tests cover capability/version gating, client eligibility,
destination filtering, the signed route and acknowledgment validation, and
failure without retry or legacy fallback.

The integrated fallback still needs hardware validation before release: deploy
matching server/agent builds in a controlled test, confirm the capability and
destination metadata, issue one voluntary request, and verify refusal leaves the
client connected as well as acceptance producing a real authorized arrival.
Do not treat an `OK` acknowledgment alone as a successful roam.
