# Network Optimizer - TODO / Future Enhancements

## SSH key placement on console gateways (udm-boot)

TABLED, and quite likely overtaken by UniFi shipping key support for console SSH themselves.

On a Cloud Gateway the gateway is also the console, so UniFi Network's Device SSH Settings does not
reach its root SSH and the public key has to be placed by hand once. We could close that loop the way
Adaptive SQM, WAN Steering and Performance Tweaks already do: use the configured username and password
to install a udm-boot script that re-places the key, so it survives firmware upgrades.

Why it is tabled rather than built:

- It does not remove the bootstrap step, only the repeat. Placing the script still needs working
  password auth, so "SSH in once" happens either way. The win is narrower than it looks.
- It is a new gateway deployment target, with its own deploy/undeploy lifecycle and a lockout failure
  mode, on top of a feature that is complete without it.

**Settle this first, it sizes the whole item:** whether `/root/.ssh/authorized_keys` actually survives
a UniFi OS firmware upgrade. `/etc/systemd/system` does, which is why udm-boot works at all. If
authorized_keys persists too, this is nearly moot and should be dropped. If it does not, it is worth
doing whatever UniFi ship. Checkable read-only on any console gateway.

## Channel Recommendation: Engine Follow-ups (recalibration-sensitive)

Everything below is TABLED - none are release-critical. These items shift the recommendation
DISTRIBUTION and the gate thresholds are calibrated to current behavior, so each needs a live
before/after on the NAS + Mac sites (and ideally a fleet sample), not a blind edit.

**Shipped baseline (through `feature/channel-guard-sibling-arms`, soaking on both test sites):**
- Measured floor #2 (candidate-only, proximity-weighted sibling, gate-scaled); propagated stress split
  from measured `HistoricalStress`; DFS penalty span-aware; per-AP fallback baseline fix.
- Spectrum-scan noise floor wired into scoring (mean-util / worst-noise, verified vs UniFi BW160);
  scans run at the radio's live BW; `ScanChannelData` keyed by (channel, width) with span aggregation.
- Cross-vantage: an AP's CURRENT channel is scored from the closest off-channel sibling, not its own
  self-contaminated read (mesh/camera traffic that follows the AP).
- Net-worse guardrail + fallback net-benefit on `ScoreAssignment`; gap-aware quick-scan prompt.
- Measured-worse guard (hold-only reconciliation): noise-floor arm, measured-interference arm with
  resident-sibling live evidence (proximity-scaled, outranks stale own outcome memory), and
  scan-utilization arm; scan-based holds feed the stale-scan re-scan prompt.
- External neighbor proxy saturated onto the measured-airtime scale (`w / (1 + w/6)`, asymptote 6.0):
  restores the absolute gates and measured floor the unbounded proxy had deadened, compresses
  neighbor-memory drift, and puts displayed scores on a meaningful 0-6-per-term scale.

**Next improvements:**

- [ ] **Spectrum data as a time series, not a snapshot.** Raw RF utilization/noise-floor is the
  strongest ground truth we have, yet it's a single sweep that ages for days while being load-bearing
  (a 13h-old reading decided a guard hold; snapshot variance forced the scan-util guard margin to a
  blunt 10 pts). Scan-radio APs produce this continuously - ingest periodically into channel memory
  as rolling per-channel averages (like the console interference metrics), then tighten the guard
  margins and retire most of the 72h staleness machinery on scan-radio sites. This also subsumes the
  older "threshold-cliff churn / smooth the external input" finding for the scan-driven terms.
- [ ] **Per-site duty-cycle calibration of the neighbor proxy.** The pooled neighbor weight measures
  audibility (propagation-grounded); the airtime it implies assumes a duty cycle. Fit the
  weight-to-measured-airtime ratio per site/band from channels that have BOTH pooled weight and
  spectrum data; keep the fixed `w/(1+w/6)` curve as the prior for blind spots. SOAK-GATED: only
  build if the fixed curve shows under/over-reaction on live sites.
- [ ] **Recompute churn.** Partly addressed. A minute-bucketed cache now keys the *scan snapshot* by
  time bucket instead of exact second, so the overview card and the channel-plan tab no longer land on
  different snapshots and produce different plans. Two things remain:
  - The cache sits on the scan fetch, not on the recommendation: `GetAllChannelRecommendationsAsync`
    still recomputes in full on every call, and the Channels page calls it from four places. Results are
    consistent now but computed several times over - coalesce or cache the recompute itself.
  - The 486 -> 626 sighting jump between adjacent runs was never root-caused. Rolling strongest-signal
    pooling plus age-decayed neighbor memory plausibly account for it, but that was not confirmed;
    if plans still differ run-to-run, start here.
- [ ] **Degradation cap shape.** `MaxApScoreDegradation` is ratio-based (1.5x), so a pristine victim
  (score 1.0) absorbs only +0.5 of new interference while an already-suffering one (6.0) absorbs +3 -
  inverted from intent, and it silently diverted two synthetic test topologies. Now that scores sit on
  a bounded scale, use an absolute cap or ratio-with-absolute-floor (pairs with
  `CatastrophicAbsoluteScore`).
- [ ] **Score legibility in the UI.** Scores are now airtime-shaped (0-6 per term) but shown as a bare
  number. Add a qualitative mapping (or approximate-airtime hint) next to the score. Cosmetic; batch
  with other Channels UI work.

**Remaining engine findings (tabled, from the earlier engine review):**

- [~] **Inconsistent objective (sum-of-ScoreAp vs ScoreAssignment).** The search minimizes
  `ScoreAssignment` (each internal pair counted once, symmetric). The auxiliary passes sum `ScoreAp`
  deltas, which count each internal pair ~2x - so a move the search rejected can be re-approved on an
  inconsistent yardstick. **Largely fixed:** the per-AP fallback now uses the `ScoreAssignment` delta
  for its net-benefit check + selection, and a global guardrail drops any final plan whose network
  score is worse than current (this hit live as a net-worse recommendation, Front Yard ch1->ch6).
  Remaining (lower priority now, backstopped by the guardrail): the altruistic "others" and comfort
  passes still use sum-of-`ScoreAp` internally - convert them too for cleanliness.
- [ ] **Position-dependence in the post-passes.** Per-AP fallback, altruistic, crowding, and comfort
  all loop in AP array order and mutate the plan in place, so earlier APs shift later APs' baselines.
  The main search is most-constrained-first; the post-passes aren't. Fix: order them deterministically
  by something meaningful (e.g. current score, most-constrained) and add a current-channel tiebreak
  when candidates are near-tied (the greedy phase already does this).
- [ ] **Global avg-improvement gate doesn't actually prevent churn.** When avg improvement <
  threshold the plan reverts to current, but the per-AP fallback and altruistic passes then run on the
  reverted plan and can still introduce moves. Decide intent: should "network not worth touching" be a
  hard stop on all per-AP moves too? (Design call - confirm desired behavior before changing.)
- [~] **Threshold-cliff churn.** All the gates are hard cliffs evaluated against scores driven by the
  rogue scan (a few dB of snapshot variance can flip a recommendation on/off between runs). Determinism
  protects against algorithmic randomness, not input variance. **Largely addressed:** proxy saturation
  compressed the dominant variance source (neighbor-memory drift now moves a dense channel's score by
  <0.35 instead of linearly). Residual: smoothing the scan-driven terms - covered by the "spectrum data
  as a time series" item above. True hysteresis/dead-band remains an option if soak still shows flips.
- [ ] **Width double-discount.** In `BuildExternalLoad` a narrow neighbor inside a wider victim is
  scaled by the width ratio (0.25x for 20-in-80) AND stored only on its narrow sub-channel - but in
  OFDM a 20 MHz interferer makes the whole 80 MHz block CCA-busy. The common case is under-weighted.
  Fix: a narrower-than-victim interferer should count against the full overlapping span without the
  width-ratio discount. Shifts external magnitudes - re-validate on the saturated scale (pooled
  weights now enter the score through `w/(1+w/6)`, so the impact is smaller than it was raw).

## Applying a channel plan to UniFi (considered, deliberately out of scope for now)

The recommendation ends at a plan the user applies by hand. That is a decision, not an oversight, and
the reasoning generalises to any mutative config operation against UniFi Network - so it is recorded
here rather than re-argued each time the question comes up.

- **UniFi Network has no PATCH.** Every change is a full-config PUT/POST, so writing one field means
  writing back the entire object - including the ~90% we have no intention of touching. Get any of
  that wrong, or have the console change it concurrently, and the site's configuration is damaged.
- **Their REST API has a history of bugs and loose form.** Parsing is not necessarily strict, and a
  release can reinterpret or break a payload shape. On read-only calls that is harmless; on a
  mutative call the same surprise can wipe an AP's configuration.
- **The blast radius is the customer's network**, which is exactly the thing this tool exists to keep
  healthy. Analysis that is wrong costs a bad recommendation; a write that is wrong costs an outage.

So today the only mutative controller operation reachable from the app is the RF quick scan
(`POST .../cmd/devmgr`) - transient and self-contained rather than a config write, though note there
is no cancel for a scan already running. No reachable feature rewrites a UniFi device or site
configuration object.

Two full-object PUT helpers do exist on the client and are currently called by nothing:
`UpdateNetworkConfigAsync` (`rest/networkconf`) and `UpdateTrafficRouteAsync` (`v2 trafficroutes`).
Wiring either one up is exactly the step this section argues against, so it should be a deliberate
decision rather than something that happens because a helper was already sitting there.

Everything else we change goes through our own SSH-deployed components, where we control the format
and the rollback.

Not a permanent no. Revisit when there is a safe path - e.g. read-modify-write with a verified
round-trip, a diff against the fetched object before sending, and a way to detect concurrent
modification. Until then, treat "we compute it, the user applies it" as the intended shape of any
feature that would otherwise write to the controller.

## LAN Speed Test

### Path Analysis Enhancements
- More gateway models in routing limits table as we gather data. `GatewayRoutingLimits` covers USG /
  USG-Pro-4, UDM / UDM-Pro / UDM-SE, and UCG-Ultra / Max / Fiber today - no UXG, UDR/UDR7 or EFG entries.
- Threshold tuning based on real-world data collection
- **Consistent wireless bottleneck attribution across test types:** LAN client speed tests show the bottleneck relative to the AP (e.g., "[AP] Back Yard (wireless)") while WAN client speed tests show it relative to the client (e.g., "[Phone] TJ iPhone (wireless)"). This is because WAN client paths reverse hops and swap ingress/egress, which flips the perspective. The wireless link is the same physical connection - both descriptions are technically correct but inconsistent. Investigate unifying to always name the AP side, since that's what users can control. Relevant code: `CalculateWanClientPathAsync` hop reversal/swap and `CalculateBottleneck` wireless link attribution.

## Alerts & Scheduling

### DST-Aware Schedule Time Display
- Schedule start times are stored as UTC hour/minute and converted to local for display using `DateTime.UtcNow.Date.ToLocalTime()`
- This uses the current day's DST offset, so a schedule created at 6:00 AM CDT (UTC-5) displays as 5:00 AM during CST (UTC-6)
- The read-only view (`FormatStartTime`) and edit form (`UtcToLocalTimeOnly`) are consistent with each other, but both shift by an hour across DST transitions
- Actual execution time is correct (UTC-based) - only the displayed local time drifts
- **Affected code:** `Alerts.razor: FormatStartTime()`, `UtcToLocalTimeOnly()`, `ParseTimeInput()`
- **Options:** Store IANA timezone per schedule, use `TimeZoneInfo.ConvertTimeFromUtc`, or store local time + timezone

### Threat Alert Dedup Tuning (if users report noise)

Current state (as of v1.5.x): Dedup is working - event-level dedup via InnerAlertId, pattern-level dedup via DedupKey with 6h merge window, rule-level cooldown at 1h. No spam reported yet, but here are levers to pull if it gets noisy:

**ScanSweep re-alerting for persistent scanners**
- Currently: Same IP re-alerts every ~2h if it keeps scanning (new events push LastSeen past LastAlertedAt, then 1h rule cooldown expires)
- Option A: Bump `attack_pattern` rule cooldown from 1h to 6h (matches the pattern merge window - one alert per scan window)
- Option B: Change `GetUnalertedPatternsAsync` to require event count increase (e.g., `EventCount > previousEventCount * 1.5`) instead of just `LastSeen > LastAlertedAt`
- Option C: Leave as-is - ongoing scanning is arguably worth periodic notification
- Trade-off: Less noise vs missing escalation of an ongoing scan that adds new ports

**DDoS alert cooldown key uses wrong IP**
- Currently: `DeviceIp = firstSourceIp` means the cooldown key is `{ruleId}:{randomSourceIp}`. For multi-source attacks (DDoS), the first source IP in the sorted list can shift between cycles, defeating cooldown.
- Fix: Use the target IP (from DedupKey `ddos:{targetIp}:{port}`) as DeviceIp for DDoS patterns, so cooldown groups by what's being attacked, not who's attacking
- Low priority since DDoS pattern dedup (DedupKey) now merges patterns correctly - this only matters if the pattern is re-detected after the 6h window

**Early-stage chain alert granularity**
- Currently: Re-alerts on more stages OR (6h elapsed AND 2x events). The `attack_chain_attempt` rule has 1h cooldown.
- If noisy: Increase cooldown to 6h, or only re-alert on stage progression (not event count growth)
- If too quiet: Reduce the 2x event multiplier to 1.5x
- Note the 2x multiplier is the *early-stage* path only; full-chain re-alerting uses 50%+ event growth
- These are Info severity - users who find them noisy can disable rule 13 in alert settings

## Security Audit / PDF Report

### Home → IoT Return Traffic Rule Suggestion
- When Home network has isolation blocking IoT, suggest adding a return traffic rule or explicit allow
- **Problem:** If Home blocks all traffic to IoT (good for security), return traffic from IoT devices won't work
  - Example: Smart TV on IoT can't respond to casting from phone on Home
  - Example: IoT device can't respond to control commands from Home devices
- **Detection:** Check for block rule Home → IoT without a corresponding:
  - Allow rule Home → IoT (with specific IPs/devices/ports), OR
  - Return traffic allow rule IoT → Home (RESPOND_ONLY / ESTABLISHED,RELATED)
- **Recommendation options:**
  1. Add specific allow rules from Home to IoT devices that need control (e.g., smart TVs, speakers)
  2. Add a RESPOND_ONLY allow rule from IoT → Home to permit return traffic
- **Severity:** Informational (user may have intentionally blocked bidirectional)
- **Context:** This is a usability issue, not a security issue - blocking return traffic is actually more secure
- **Building blocks exist:** `FirewallRule.AllowsOnlyReturnTraffic()` and `BlocksNewConnections` already
  recognize RESPOND_ONLY rules (used today only to suppress shadowing warnings), so the detection side is
  mostly a matter of inverting an existing check.

### Third-Party DNS Firewall Rule Check
- When third-party DNS (Pi-hole, AdGuard, etc.) is detected on a network, check for a firewall rule blocking UDP 53 to the gateway
- Without this rule, clients could bypass third-party DNS by using the gateway directly
- Implementation: Look for firewall rules that DROP/REJECT UDP 53 from the affected VLANs to the gateway IP
- Severity: Recommended (not Critical, since some users intentionally allow fallback)
- **Distinct from what already ships:** `DnsSecurityAnalyzer`'s port-53 block detection targets the
  External zone, and DNS-LEAK-001 covers leaks to the internet. Neither checks the *gateway-directed*
  bypass this item is about; the third-party finding only emits generic "consider enabling DNS firewall
  rules" copy with no rule verification behind it.
- **Status:** Awaiting user feedback on current third-party DNS feature before implementing

## Performance Audit

New audit section focused on network performance issues (distinct from security audit).

### Port Link Speed Analysis
- Crawl the entire network topology and identify port link speeds that don't make sense
- Reuse the logic from Speed Test network path tracing
- Examples of issues to detect:
  - 1 Gbps uplink on a switch with 2.5/10 Gbps devices behind it
  - Mismatched duplex settings
  - Ports negotiated below their capability (e.g., 100 Mbps on a Gbps port)
  - Bottleneck chains where downstream capacity exceeds upstream link
- Display as performance findings with recommendations

### Path-Based MTU Mismatch Detection
- The *config-level* half already ships: `PerformanceAnalyzer.CheckJumboFrames` suggests jumbo frames
  (trigger: 2+ access ports at 2.5 GbE or faster, infrastructure trunks excluded) and flags the
  global-on/device-off and partially-enabled cases as an MTU mismatch that can cause fragmentation.
- Still open is per-path measurement, which needs SSH rather than config inspection:
  - During path tracing, SSH into each hop (gateway, switches) to query interface MTU
  - Gateway: `ip link show <interface>` or parse `/sys/class/net/<iface>/mtu`
  - Switches: Check port MTU via SSH (UniFi switches support shell access)
  - Compare MTU values across the path - all devices should match
- Issues this would add over the config check:
  - Intermediate device with lower MTU than endpoints (causes fragmentation)
  - Jumbo Frames enabled on LAN but not on inter-switch uplinks
  - VPN/tunnel overhead not accounted for (e.g., WireGuard needs ~1420 MTU)
- Display: Show MTU at each hop in path analysis, flag mismatches (no `Mtu` member exists on
  `NetworkPathAnalyzer` / `PathAnalysisResult` today)
- Severity: Warning (mismatches cause performance degradation or silent drops)
- Prerequisite: Reuse SSH infrastructure from SQM/gateway speed tests

### WiFi Optimizer Enhancements
- **Channel recommendation: broaden search candidate generation (long-term, the real fix).** The exhaustive/greedy search evaluates a small candidate set per AP (e.g. ~2 channels/AP → only 8 assignments evaluated for a 4-AP 5 GHz site), so it can miss the globally optimal assignment. Worth being precise about the cause: the set is not arbitrarily pruned - `GetValidChannelsWithWidth` returns *every* regulatory-valid channel, but only at the AP's current width and deduped by bonding group (width reduction is marked "future feature" in that code). So "richer candidate set" mostly means exploring across widths - notably an "altruistic" move where relocating a still-healthy AP declutters a worse neighbor (e.g. move a fine AP off a shared 160 MHz block so a congested one stops sharing it). Today that gap is patched by an altruistic relocation pass in the per-AP fallback (`ChannelRecommendationService`, gated on site-wide score improvement), but the correct long-term fix is for the search itself to consider a richer candidate set per AP (e.g. all valid non-DFS blocks plus historically-good channels, with branch-and-bound pruning to keep the space tractable) so the global optimizer finds these moves directly and the fallback becomes a safety net rather than the source of the recommendation. When this lands, revisit whether the altruistic fallback pass is still needed.
- **Power & Coverage: per-band signal classification (aggregates only)** - the per-client half shipped: a
  shared `SignalClassification` helper exists and PowerCoverageAnalysis classifies each client by its
  actual band for the percentage/count aggregations. What still hardcodes `RadioBand.Band5GHz` is the
  aggregate layer - the avg/min/max stat cards and the signal-distribution bucket chart - because those
  operate on aggregate values without per-client band context. The bar chart would need to either split
  by band or color each client's contribution by their band. No regression today, just a missed opportunity.
- **MLO per-AP detection in the optimizer** - the mechanism already exists for path analysis:
  `NetworkPathAnalyzer` sets `hop.MloEnabled` by matching each AP's `vap_table` SSIDs against the
  MLO-enabled WLANs. The WiFi Optimizer health issue has not adopted it - `WiFiOptimizerService` still
  flags MLO from `hasWifi7Aps && hasMloEnabledWlan` (any AP + any WLAN), so an AP that broadcasts no
  MLO SSID is still counted. Reuse the path-analysis pattern.
- **MLO STR mesh backhaul (multi-band):** Channel recommendations pin a mesh child to its leader's channel only on the single band `AccessPointSnapshot.MeshUplinkBand` reports. AP-to-AP MLO STR backhaul can run over multiple bands at once (e.g. 5 + 6 GHz). When UniFi exposes per-link bands, make `MeshUplinkBand` a set and have `BuildMeshConstraints` emit one constraint per participating band. The reconciliation logic in `ChannelRecommendationService` keys off `MeshGroupLeader` and needs no change - only the constraint-building. See `TODO(MLO)` in `BuildMeshConstraints`. Dormant: no AP-to-AP MLO STR backhaul hardware exists yet (today's MLO STR is client/bridge only - UDB-Switch and AirWire, the MLO STR bridge - which are endpoints, not mesh-AP children, so they never hit this path).

### AP Catalog: Enforce 5 GHz EIRP Cap (US Regulatory)
- FCC caps EIRP at 36 dBm for 5 GHz non-DFS (UNII-3, ch 149-165) and 30 dBm for UNII-1 (ch 36-48)
- The TX Power by Access Point section currently shows uncapped EIRP (TX + gain), which can exceed 36 dBm for high-gain models, implying there's TX power headroom when there isn't
- Already handled for some models on 6 GHz (E7-Campus, E7-Audience have EIRP-aware TX caps in catalog)
- **Affected 5 GHz models (TX + gain > 36):**
  - U7-Outdoor directional: 26 + 13 = 39 (cap TX to 23)
  - U7-Pro-Outdoor directional: 26 + 11 = 37 (cap TX to 25)
  - E7-Campus: 30 + 12 = 42 (cap TX to 24)
  - E7-Audience narrow: 30 + 15 = 45 (cap TX to 21)
  - E7-Audience wide: 30 + 11 = 41 (cap TX to 25)
  - UWB-XG narrow: 25 + 15 = 40 (cap TX to 21)
- **Options:**
  1. Cap MaxTxPowerDbm in the catalog so TX + gain <= 36 for all 5 GHz entries (like we do for 6 GHz on E7 models)
  2. Add regulatory-domain-aware EIRP capping in the display/calculation layer (more complex, handles UNII-1 vs UNII-3 differently)
  3. Show "regulatory max EIRP" alongside "hardware max EIRP" in the UI
- Option 1 is simplest and matches the existing 6 GHz pattern. Option 2 is more accurate but needs channel-to-sub-band mapping.
- **Mechanism is already there:** `ModeOverrides` carries per-mode `MaxTxPowerDbm` / `DefaultTxPowerDbm`
  (that is how E7-Audience's 6 GHz caps work), which is what Option 1 needs - several of the 5 GHz
  offenders only exceed 36 dBm in their directional/narrow antenna mode, not omnidirectional.
- **Note:** DFS channels (UNII-2/2C) have lower limits but are dynamic - firmware handles those

### Floor Plan Heatmap - Per-Channel Frequency
- Current heatmap uses a single center frequency per band (2437, 5500, 6500 MHz)
- 5 GHz spans 5150-5850 MHz (channels 36-165), ~1 dB FSPL difference at the extremes
- Material attenuation also varies across the band range
- Implementation:
  - Add `Channel` (or `FrequencyMhz`) to `PropagationAp` from UniFi radio config
  - Map channel number to center frequency (e.g., ch 36 = 5180, ch 149 = 5745)
  - Pass actual frequency to `ComputeSignalAtPoint` instead of band center
  - Update `MaterialAttenuation` to interpolate between band values if needed

### Floor Plan Heatmap - Channel Bandwidth & Per-Client Signal Modeling
- Current heatmap shows raw RSSI (dBm) with no awareness of channel bandwidth
- Wider channels raise the thermal noise floor, reducing effective SNR and usable range:
  - 20 MHz: -96 dBm noise floor, 40 MHz: -93, 80 MHz: -90, 160 MHz: -87, 320 MHz: -84
  - (assumes ~5 dB receiver noise figure)
- A -80 dBm signal gives 16 dB SNR on 20 MHz (decent) but only 7 dB on 160 MHz (unusable)
- Noise floor formula: -174 + 10*log10(BW_Hz) + NF_dB

#### Per-Client Channel Width Negotiation (critical nuance)
- 802.11 negotiates channel width per-client based on capabilities. The AP does NOT force a
  single channel width on all clients. A 160 MHz AP transmits to an 80 MHz client using 80 MHz.
- From the client's perspective, the noise floor matches ITS supported width, not the AP's config:
  - Client supports 80 MHz on a 160 MHz AP -> client sees -90 dBm noise floor, not -87 dBm
  - Client supports 40 MHz -> sees -93 dBm noise floor regardless of AP config
- The client's receiver only processes its supported bandwidth. The extra spectrum the AP has
  configured is simply unused for that client's transmissions.
- This means UniFi Design Center's heatmap (and our current one) shows worst-case coverage for
  clients negotiating the FULL configured width - which are typically the newest devices sitting
  close to the AP where it doesn't matter anyway. The heatmap makes it look like coverage is
  bricked when most clients actually have much better coverage than shown.
- Real-world: most clients are 80 MHz capable. Configuring 160 MHz gives 80 MHz coverage
  footprint for those devices plus throughput bonus for 160 MHz clients when close enough.
- Downsides of wider AP config: consumes more spectrum (matters for multi-AP channel planning),
  and DFS events on the secondary 80 MHz segment can force the whole channel to shift,
  briefly disrupting all clients including 80 MHz ones.

#### Implementation
- Add `ChannelWidthMhz` to `PropagationAp` (pull from UniFi radio config)
- **Default view**: show coverage based on the AP's configured channel width (current behavior
  plus bandwidth-aware color thresholds) - this is the conservative/worst-case view
- **Per-capability tier view**: let users toggle between client capability tiers to see what
  coverage actually looks like for their devices:
  - "160 MHz clients" (worst case, smallest coverage)
  - "80 MHz clients" (most common, realistic coverage)
  - "40 MHz clients" (older devices, best coverage)
  - "20 MHz clients" (legacy, maximum coverage)
  The selected tier overrides the AP's configured width for noise floor and color threshold
  calculations. Signal strength (RSSI) stays the same - only SNR interpretation changes.
- Alternatively/additionally, offer an SNR view mode that shows signal quality (dB above noise
  floor) rather than raw power (dBm), making bandwidth impact visually obvious
- Consider showing a summary callout: "Most of your clients support 80 MHz - here's what they
  actually experience" to educate users about the per-client negotiation reality

#### Leftover from the v1.x feature set
Everything the old "Implemented Features" checklist tracked is live and verified (utilization per AP,
AP load balance, interference detection, band steering, connectivity flow, legacy-client airtime impact,
site health score, power/coverage). One gap hides inside the "signal strength / SNR per client" claim:
per-client *signal* classification is everywhere, but `WirelessClientSnapshot.Snr` has no UI consumer at
all - it is collected and never shown. Either surface it or drop the field.

## SQM (Smart Queue Management)

### Retrofit Custom Cloudflare Speed Test Binary into Adaptive SQM
- Replace current WAN speed test approach in Adaptive SQM with the custom Cloudflare speed test binary
- The Cloudflare speed test provides more accurate and consistent WAN throughput measurements
- Integration points: SQM calibration, periodic re-calibration, manual speed test triggers
- Should use the same binary/approach as the standalone Cloudflare speed test projects

### Multi-WAN Support
- Support for 3rd, 4th, and N number of WAN connections
- **Detection is already there** - `SqmService` walks wan1..wan6. What is hardcoded to two WANs is
  everything downstream: `SqmDeploymentService.DeploySqmMonitorAsync(wan1Interface, wan1Name,
  wan2Interface, wan2Name)`, `TcMonitorClient`'s `Wan1`/`Wan2` fields, and the `Sqm.razor` UI
  (`wan1Config`/`wan2Config` throughout). Open work = monitor deployment, status plumbing, and UI.

### GRE Tunnel Support (Cellular WAN)
- Support GRE tunnel connections from cellular modems (U5G-Max, U-LTE)
- These create GRE tunnels that should be treated as valid WAN interfaces for SQM
- **Partly prepared:** WAN extraction already recognizes GRE virtual WANs and models their null
  `PhysicalIfName` / `LinkSpeedMbps`. Nothing GRE-specific exists in deployment or shaping, and no GRE
  tunnel has been shaped end to end - that is the open part.

## Monitoring

### PON supplemental counters: wrap-aware deltas (SFP Stats)
The augmented PON provider's frame/allocation counters (GEM tx/rx, LAN frames, allocations) are 32-bit
on the ONT and wrap (~4.29B unsigned, or ~2.15B if signed). Every path handles the wrap **safely**
today - the chart delta guard (`cur >= prev ? cur - prev : null`), the alert spike check
(`if (delta < 0) delta = 0`), and ISP Health's positive-increment total all treat a wrap like a
counter reset. The only cost is a single dropped data point on the frame charts at each wrap, which on
a busy link can be ~hourly for the high-rate counters.

Follow-up (low priority): reconstruct the true delta across a wrap instead of gapping it. Use
`sfp_uptime_s` to distinguish a wrap (uptime kept climbing) from a real reset/reboot (uptime dropped),
then add the modular span. Blocker: we'd have to know the counter width (2^31 vs 2^32) per field, and a
wrong guess produces a garbage spike - so the safe null-gap stays the default until the widths are
confirmed. Not worth it for a rare one-point gap; revisit if the frame charts read as too sparse.

### Upstream path discovery: capture every responding hop's ASN (transit attribution)
Transit Health involvement weighting and destination->transit jitter absolution both key off which
monitored internet targets a transit ASN provably carries (trace ancestry).

The target side of this shipped: AWS DynamoDB regional endpoints are resolved, geo-ordered and
latency-ranked to the nearest region(s) (they are not anycast), capped by `MaxAwsPathEndTargets` and
persisted as path-end targets. They ride paid transit, unlike the CDN/DNS targets that peer at the
local IX and therefore gave the attribution nothing to work with.

What remains is the attribution itself: `PersistHopOrderAsync` skips responding hops that are not
monitored targets, so the ancestry still cannot tell WHICH transit ASN a path crossed. Record every
responding hop's ASN, not just monitored-target IPs. Note the ceiling: in testing the intermediate
transit hops frequently do not answer traceroute at all, and pure-star segments recover nothing no
matter how the ancestry is stored.

### Investigation Functions (Network Performance tab)
The Investigate card currently jumps the latency charts to the most recent **packet-loss** and **loaded-loss** events and steps event-to-event (coalesced, peak-loss minute). Ideas to extend it:

**Reuse detectors ISP Health already computes (low effort - mostly wiring existing results into `navigateToTime` + buttons):**
- **Congestion events** - `CongestionLocalizer` already produces events with disposition (confirmed / self-inflicted / control-plane-noise) and scope (hop / ASN / shared). Add an Investigate button that jumps the latency charts to each event, with the highlight band colored shared-vs-local like the ISP Health chart. Strongest detector, currently invisible on these charts.
- **Path-shift events** - `StepChangeDetector` already finds RTT step changes; jump to the step with a "+N ms" label.
- **Outages** - `OutageDetector` already classifies full / partial / brief; jump to the outage window and show the recovery shape.
- **Unify the two views** - the loss factor headers already deep-link via `?investigate=`; the ISP Health "Path & Congestion Events" timeline items are still plain non-clickable divs. Make them deep-link the same way. (Half-done - the mechanism exists, only these items are unwired.)

**New detections (more work):**
- **Bufferbloat events** - loaded *latency* spikes (the `latencyTriggered` signal), distinct from loaded loss; bufferbloat often has zero loss, so the loss buttons miss it.
- **Jitter spikes** - sustained P95 jitter excursions.
- **Saturation events** - when WAN throughput pegged at/over the configured plan; pairs with loaded loss to answer "was the line actually maxed."

**UX upgrades to what exists:**
- **"Worst in window" vs "most recent"** - a jump-to-peak mode that lands on the highest-loss event directly instead of stepping from the latest.
- **Verdict line on landing** - when landing on a loss event, append what it was (loaded vs idle, which hop/ASN, congestion disposition) so "0.7% loss" becomes "0.7% loss, self-inflicted bufferbloat at your access egress." The localizer already computes this.
- **Per-target investigate** - click a target in the stats table to step through just that target's events.

Suggested order: congestion / path-shift / outage navigation first (cheap, consistent), then the verdict line (makes every investigation land with an explanation, not just a number).
- **Relevant code:** `MonitoringInfluxClient.FindRecentLossEventAsync` / `FindRecentLoadedLossEventAsync` (+ `SelectBoundaryEvent` coalescing), `Monitoring.razor` Investigate card + `NavigateToLossEvent`, `latency-charts.js` `navigateToTime` / `buildInvestigateAnnotations`, `IspHealthService` (`CongestionLocalizer`, `StepChangeDetector`, `OutageDetector` outputs already on the report).

### Multi-WAN Support (ISP Health & NMS)
- ISP Health currently grades a single (primary) WAN. Several inputs are read globally rather than scoped to the WAN being scored:
  - **Upstream ancestry / `hopOrderKnown`** (`IspHealthService.ComputeAsync`): `UpstreamDiscoveries` are queried across all WANs. Rows carry `WanInterface`, and the tracer persists per-WAN, but the scorer reads them globally - so a second WAN's discovery data can flip the jitter-absolve gate (and the routes-through witnesses) for a WAN that has no ancestry of its own. Scope the discovery query and `hopOrderKnown` by `WanInterface`.
  - **Targets / series / rates** are likewise resolved for the primary WAN only; per-WAN scoring needs each WAN's own targets, latency series, throughput, and expected speeds.
  - **Throughput and expected speeds can pair the wrong link** (`ResolveWanCounterAsync`): counters resolve to the CONFIGURED primary's `CounterIfName` but fall back to the ACTIVE uplink (`DiscoveredDevice.WanInterfaceNames`), while `ResolveExpectedSpeedsAsync` always returns the configured primary's plan speeds (falling back to `SqmWanConfigurations` WAN1). On the fallback path that divides active-WAN bytes by the primary WAN's plan: a 100 Mbps LTE failover against a 1 Gbps fiber plan reads ~10% utilization. The load figure is not cosmetic - `avgLoad` sets the Packet Loss ceiling quadratically (`ScorePacketLoss`), `LoadClassifier` splits loaded from idle samples, and `CongestionTopology.Load` drives `LoadCoincident` - so understated load grades a saturated link against the strictest (idle) loss ceiling.
  - **Load context goes dark during failover** even on the non-fallback path: probes traverse the secondary WAN while the primary's counters read ~0, so the entire window classifies as idle. Load context has to key off the WAN the probes actually used, not the configured primary.
  - **The usage fingerprint is the one input that SHOULD span all WANs** (`BuildUsageFingerprintAsync`): it asks "was the user doing anything in this hour", not "how loaded is the link being graded". It is primary-only today, so hours carried by a secondary read idle, understating those hour bins and softening outage `UsageWeight` toward `UsageWeightFloor`. Give it its own all-WAN query when the rest goes per-WAN.
  - **Latent trap:** `MonitoringInfluxClient.QueryGatewayWanRatesAsync` sums whatever interface list it is handed (`group([_time,_field]) |> sum()`). Nothing sums today because every ISP Health caller passes exactly one name - `GetWanInterfaceNames` returns a single active-uplink counter interface despite the plural name - but if that ever widens, every load computation silently becomes total-WAN over primary-WAN-plan with no error. `Monitoring.razor` `GetWanRates` carries a "do not widen this" comment; the ISP Health resolver has no equivalent guard.
- Plan: grade ISP Health per-WAN (one report per active WAN), keyed by `WanInterface` end-to-end, and surface a per-WAN selector in the UI. Until then, secondary WANs are not separately graded.
- **Relevant code:** `IspHealthService.ComputeAsync` (TODO marker at the discoveries query), `UpstreamTracerService.PersistHopOrderAsync` (already per-WAN), `MonitoringPathView` (already WAN-scoped), `IspHealthService.ResolveWanCounterAsync` / `QueryWanRatesAsync` / `BuildUsageFingerprintAsync` / `ResolveExpectedSpeedsAsync`, `IspHealthScorer.ComputeAverageLoad` + `ScorePacketLoss`, `MonitoringInfluxClient.QueryGatewayWanRatesAsync`.

### Segmented Loaded Latency: Access (ISP) vs Transit
- The loaded-latency factor produces a single "+N ms under load" figure today. With hop ordering known (`hopOrderKnown` + `ancestorIpsByTargetId`), the loaded rise can be **attributed by path segment**: the elevation on hops inside the ISP's access ASN vs. the *additional* elevation that only appears on hops **beyond** that ASN (transit/peering). The transit-segment value is the corroborated loaded delta at downstream transit hops minus the delta already present at the ISP-ASN boundary.
- **Why it matters:** separates *local access-link bufferbloat* (the last-mile queue the ISP's SQM/QoS can fix) from *congestion introduced after the ISP's ASN* (peering/transit saturation the ISP can only fix via its upstreams). Today both collapse into one number, so a user can't tell "my access link is bloating" from "my ISP's transit is congested under load."
- Rides on the loaded-latency propagation model (a real bottleneck elevates its hop and everything downstream): the access-segment delta is the elevation shared from the access hop downstream; the transit-segment delta is the *extra* elevation that first surfaces past the ISP-ASN boundary. A purely-transit increase with a clean access segment is the "more is being introduced after your ISP's ASN" case.
- Surface as two sub-factors (or one factor with an access/transit breakdown) so the dashboard can say "+X ms access, +Y ms transit."
- **Relevant code:** `IspHealthScorer` loaded-delta computation (`AccessHopSeries` vs `TransitAsnSeries`), the ancestry/hop-order inputs, and the `AccessIsp` / `Transit` `MonitoringTargetType` split already present in the data.

### Gap-Gated SNMP Counter Reset Detection
- `InterfaceRateCalculator` currently distinguishes a genuine counter reset (device reboot) from a single corrupt SNMP read by requiring two consecutive below-baseline reads before reseeding the baseline (`ResetPending` → `ResetConfirmed`). A discarded over-ceiling rate then advances the baseline so nothing can wedge an interface.
- Cleaner discriminator: the **elapsed gap**. A real reset only follows a reboot, which trips ~5 consecutive SNMP failures (~25 s) and a 5-minute exclusion, so the first sample back has a large elapsed gap (~5 min). A corrupt-read glitch arrives at the normal ~5 s cadence with a tiny gap. So: backwards counter with a large gap (e.g. ≥ 60 s, above poll jitter and below the exclusion window) → reset, reseed immediately; backwards counter at normal cadence → glitch, hold the baseline and suppress. This reseeds genuine resets in one poll instead of two and makes the rare two-fast-corrupt-reads false-confirm impossible by construction.
- **Not required** - the confirmed-by-repeat version is correct and the worst edge (double glitch) self-heals in ~10-15 s with no spike written. Revisit only if logs show clusters of "Discarding implausible SNMP rate" WARNs that aren't explained by a single bad read. Keep a fallback for the (unrealistic) small-gap reset so nothing can wedge.
- **Relevant code:** `InterfaceRateCalculator.Compute` (reset/candidate branch), `MonitoringCollectionAgent.WriteInterfaceCounters`.

### Monitoring Interfaces: Alias IP follow-ups

Both shipped pieces are live: `AliasIp` on `MonitoringInterface` with fwmark/DNAT policy routing
(verified on real UCGF hardware against two devices sharing `192.168.100.1` on different WANs), and the
`StarlinkWanDetector` advisory that steers users to native Starlink Stats instead of creating an
interface for the dish.

Recorded so it is not re-litigated: the earlier "warn when a natively-monitored WAN is picked as the
plain side" idea was investigated and dropped. UniFi's Starlink widget exists only in Cloud Gateway
console builds, and its dish poller binds its sockets to the Starlink WAN's interface address rather
than depending on the main routing table (the UniFi OS Server build of the same app version contains no
Starlink code at all). The claimed main-table breakage was never reproduced, and the real dual-WAN
hazard - a plain interface on the OTHER WAN claiming the dish's shared IP - is already handled by alias
mode and its stale-route teardown.

One follow-up idea remains open (not built):

- **LAN-client passthrough to the real IP.** An aliased device's own web UI can hard-redirect a browser to its real IP once loaded (anti-DNS-rebinding check), so a human browsing the alias hits a dead end unless something *also* routes their specific LAN IP to the real target. Works today via a manual, non-persistent `ip rule` reusing the alias's private table (`ip rule add from <client-ip> to <real-ip>/32 lookup <table>`). A built-in version is plausible (one extra source-scoped per-row `ip rule` in the boot script, allowlisted to admin-specified LAN IPs) but deliberately **not built** - a "grant direct real-IP access" knob next to a mechanism whose entire point is to never expose the real IP invites the exact dual-WAN misconfiguration this feature prevents. Gauge demand first.

### Topology / Status History
- Placeholder for historical topology / connectivity replay (device + link state over time).
- **When this lands, the topology/map JS needs full device-status support.** Today those spots
  consume a single online/offline bool - `lan-flow-map.js` / `lan-flow-map-2d.js` (`node.online`),
  `floorPlanEditor.js` (`ap.online`), and the SpeedTest coverage map (`apData.isOnline`) - so a
  provisioning/updating device renders offline-grey. The C# side already centralizes this in
  `UniFiDeviceStateMap` -> `DeviceStatus` (Online / Transitional / Offline / Error); thread that
  through the marker DTOs and color markers yellow (provisioning) / red (error) instead of the
  binary bool. The status-dot/badge/card spots already do this - only the JS map markers remain.

### Nokia ONT: confirm coverage across other models

`NokiaXs010xOntProvider` was built and verified against one XGS-PON **XS-010X-Q** (owner-confirmed). The `GponForm` web API (`Login_GetConfig` nonce/salt SHA-256 login -> `getUpdateinfo`) appears common to Nokia's box ONTs, but this is unconfirmed on any other unit, and the provider hardcodes both the model string and `PonType = "XGS-PON"` because the device reports neither its own model nor a line rate.

- [ ] Confirm whether the same flow works on Nokia's **GPON box siblings** (e.g. G-010G-Q). If yes, either relax the hardcoded XGS-PON label or split into a GPON variant - a GPON unit on this provider would currently be mislabeled XGS-PON.
- [ ] Confirm whether it works on Nokia's **SFP-form ONTs** (the SFP stick modules), which may expose a different web/telnet interface entirely.
- [ ] If coverage is broader than one model, revisit the dropdown label ("Nokia XS-010X-Q (HTTP)") and the hardcoded `DeviceModel` so multi-model units aren't misreported.

## Multi-Tenant / Multi-Site Support

### Agent SNMP Watchdog (detect wedged console snmpd, opt-in auto-restart)

UniFi gateway `snmpd` recurrently wedges: process alive and idle in `select()`, sockets
still bound, but answers nothing - not even `sysUpTime` from localhost (seen on multiple
gateways; diagnosed live on the UDR7 test gateway 2026-07-24). Today this reads as silent
stat gaps.

Not to be confused with what already exists: `SnmpFailureTracker` + `SnmpRunner` count consecutive
failures and exclude a failing device from polling for a while (surfaced as `SnmpPollState.Excluded` on
the Monitoring Setup dashboard). That is failure-driven backoff - it stops polling entirely and never
compares against ICMP. None of the four items below are covered by it. Summary of the design:

- [ ] **Detect (all agents, default on):** in `SnmpRunner`, count consecutive per-device
  poll failures; when a device misses ~60 s of SNMP (12 misses at the 5 s tier) while its
  ICMP probe still answers (ProbeRunner already knows), transition to `SnmpDown` and report
  a distinct status over the tunnel instead of silent gaps.
- [ ] **Surface (server):** Alert Rule "SNMP unresponsive on <device> while device is
  online"; Device Stats shows "stale since" rather than a quiet flatline.
- [ ] **Recover (on-gateway agents only, opt-in, default detect-only):** `pgrep snmpd` ->
  `kill` and let `ubios-udapi-server` (its parent/supervisor) respawn it. Guard rails:
  only after confirmed SnmpDown >= 60 s, max 1 restart per 10 min, Information-level log,
  NEVER touch udapi itself. Off-gateway agents stay detect-only.
- [ ] **Back off while down:** drop the device to the medium poll interval until it
  answers again.
- Plumbing: status/error field on the SNMP result message in `AgentProtocol` (proto change
  -> NAS server + all test agents redeploy together), failure counter in `SnmpRunner`,
  server alert rule, site-settings toggle for auto-restart.

### Multi-Tenant Architecture

Largely shipped, and via a different mechanism than this item originally proposed. Live today:
`Sites` / `SiteAgents` tables behind a `MultiSiteEnabled` flag, per-site databases under
`data/sites/<slug>`, per-site console connections (`SiteContextService`, `UniFiConnectionService`), a
site switcher, and an aggregate `/sites` page.

The original plan - a VPN/tunnel to each customer network requiring a unique non-overlapping IP
structure per client - was superseded by the dial-out agent + gRPC tunnel + proxy architecture, which
does not care about overlapping subnets. Do not resurrect the VPN framing.

Remaining, and tracked elsewhere in this file: real tenant *isolation* (authentication, per-site
permissions) is the identity work, not this item; site-specific alert channels and site lifecycle
have their own sections.

### Agent Tunnel Security Hardening

The on-site agent gives the central server SSH reach into each site's gateway, and a
gateway is the LAN router, so that reach is effectively LAN-wide. That makes the
**central server the highest-value target** and its hardening the priority. A
compromised central server is inherent game-over for the gateways it manages - the
shadow of centralized gateway management, not a tunnel flaw - so protocol-level
controls can't fix it; protecting the server is what matters. (See the agent README
"Security and hardening" section, and the comments on `ProxyHandler` and
`GatewaySshService.MaybeRouteViaAgentAsync`.)

Worth doing - defends the one risk that does NOT already require owning the server
(a leaked `agentKey` used standalone):
- [ ] One live tunnel per `agentKey`: reject a second concurrent connection and alert
  rather than silently accepting it. Checked - `AgentTunnelRegistry.Register` does NOT
  enforce this: it `AddOrUpdate`s, so a second connection silently displaces the first
  (documented there as "a reconnect replaces the previous connection's channel"). A stolen
  key therefore evicts the legitimate agent with no signal. `AgentConnectionAlertMonitor`
  only alerts on agent-offline, so the eviction surfaces at best as a blip.
- [ ] Alert on a new public source IP for an existing agent (the tunnel already sees it
  on connect). This is the anomaly signal for a stolen key.
- [ ] Surface each agent's public source IP in the UI, so IP-allowlist maintenance is a
  one-line firewall edit when a site's WAN IP changes.
- [ ] (lower priority) Soft host-key tripwire: record the last-seen gateway SSH host key
  and alert on change, but never block. Turns a silent MITM into a logged event while
  tolerating UniFi's key cycling.

Deployment guidance (now documented in the agent README + `docker/DEPLOYMENT.md`):
IP-allowlist BOTH the admin plane and the agent tunnel endpoint to your sites' public
IPs. Commercial and even residential sites are stable enough in practice; a stolen key
from a random IP then dies at the firewall before the bearer key is presented. Bearer
key + rate-limiting stay as defense-in-depth behind it.

Implemented (agent-owned proxy controls; revises an earlier "not worth it" call):
- **Site-local proxy dial fence** (always on): `ProxyOpen` targets must be RFC1918 /
  IPv6 unique-local / IPv6 link-local; hostnames are resolved once, every address
  validated, and the dial uses the validated addresses. The earlier rationale ("gateway
  SSH pivots anyway, so an allowlist contains nothing") was right about LAN containment
  but missed the **internet-relay vector**: unrestricted `ProxyOpen` let a compromised
  server use every site as a silent exit node against third parties - no gateway creds
  needed, no footprint on customer equipment. The fence closes exactly that, and only
  claims that.
- **Operator pin** (`proxyAllowedCidrs` in agent.json): replaces the fence with an
  operator-owned CIDR list - real reach-capping the server can't override, and the only
  escape hatch for public-IP targets.
- **Dial audit trail**: every proxy dial (allow + deny) journaled agent-side where the
  server can't suppress it.
- NOT built (still on record as not worth it): a **server-pushed device allowlist**
  (UniFi device list + custom targets enforced agent-side). The server is authoritative
  for tunnel config, so a compromised server pushes its own list; signing doesn't help
  (server holds the key) and TOFU/ratcheting either blocks legit changes (device
  adoption, DHCP renumbering) or degrades to log-only. All complexity, no unforgeable
  containment - the three controls above are the honest subset.

Considered and deliberately NOT implemented (rationale on record so it isn't re-litigated):
- **Hard SSH host-key pinning**: impractical. UniFi regenerates SSH host keys on firmware
  upgrades (and adoption/factory reset), so a strict pin breaks SSH after routine updates
  and trains operators to click through warnings - worse than the soft tripwire above.
- **Agent-side SSH command filtering**: impossible at the agent - the proxied SSH session
  is encrypted end-to-end between the central server and the gateway's sshd; the agent
  pumps opaque bytes. Command safety lives server-side (parameterized command
  construction); the gateway-side option is `authorized_keys` forced commands.

### Agent Tunnel Store-and-Forward: Robustness Follow-ups (#978 steady-state review)

The ack-based result buffer shipped in #978 is sound in steady state; these are edge-case behaviors
found in review, none blocking. (The review's top finding - agent clock skew silently disabling a
site's alerting via the AlertFreshness gate - is fixed: `AgentProbeResultSink` now warns hourly per
site when samples on a long-connected tunnel skip alert evaluation.)

- [ ] **Poison frame becomes a reconnect-replay loop.** A frame that deterministically throws in
  `RecordBatchAsync`/`RecordSnmpBatchAsync` (corrupt site DB, disk full, throwing Influx config)
  tears down the tunnel read loop, never gets acked, and replays from the front of the buffer on
  every reconnect - head-of-line blocking newer data until the 12 h age cap evicts it. Pre-#978 the
  frame was simply lost on teardown, which "recovered". Fix shape: per-frame try/catch that acks
  (or dead-letters with a log) after N failed attempts, so one bad frame can't wedge the site's
  pipeline. Transient errors (DB lock, Influx blip) should still retry, so N > 1.
- [ ] **Ack precedes durable persistence.** The server acks after `RecordBatchAsync` returns, but
  Influx writes may still sit in the client's write buffer - a server crash in that window loses
  data the agent has already trimmed. Narrow window, crash-only. Options: ack after an Influx
  flush, or accept and document the window (probably fine for monitoring data).
- [ ] **False-stale blip window.** `StaleThreshold` (45 s) over a 30 s heartbeat leaves 15 s of
  margin. A healthy agent silent >45 s (host suspend, long GC pause, forward server clock step)
  gets proxy opens refused and its console flipped to awaiting-agent; recovery is the next
  heartbeat plus up to 60 s for the config-refresh loop to reconnect the console. The open breaker
  already clears instantly on fresh inbound - consider un-flipping the console on fresh inbound
  too, instead of waiting for the 60 s refresh tick.
- [ ] **Buffered non-result frames are never directly acked.** `SnmpOidResult` (OID test replies)
  ride the ResultBuffer, but the server only acks ProbeResults/SnmpResults frames - other frame
  types are trimmed only cumulatively by a later result ack. On a quiet site (no monitoring flow)
  one can linger and replay on every reconnect until the 12 h age cap. Harmless today (the server
  ignores stale request ids), but acking every sequenced frame uniformly removes the class.
- **Relevant code:** `AgentTunnelService` read loop (ack sites, `WatchLivenessAsync`),
  `TunnelClient.DrainResultsAsync`, `ResultBuffer`, `AgentProbeResultSink` (`AlertFreshness`,
  `NoteAlertEvaluationSkipped`), `AgentTunnelProxyService` (open breaker),
  `UniFiConnectionService.NoteTunnelUnreachableAsync`.

### Credential Key: Hardening Follow-ups

Self-hosted project, so keep this proportional. The at-rest credential key is a random
file (`.credential_key`). `NO_CREDENTIAL_KEY_FILE` (implemented) + a Docker secret is the
pragmatic answer and probably enough: it keeps the key off the data volume, which is the
main win. DEPLOYMENT.md documents that and warns that the data volume - and `.nopt`
config exports, which bundle the key - are secret material.

Optional, only if there's real demand (don't gold-plate a self-hosted tool):
- [ ] Envelope encryption against an external secret manager (Vault / cloud KMS) for the
  rare operator already running one - master key never on disk, rotation + audit for free.
  Nice-to-have, not a priority.
- [ ] `.nopt` export wrapper uses a hardcoded obfuscation key (`ConfigTransferService`),
  and the archive includes `.credential_key`. Fine as long as exports are treated as
  secret (documented), but a passphrase-encrypted export option would let users share/store
  them less carefully. Low priority.
- [ ] The ASP.NET Data Protection keyring (`Program.cs` `PersistKeysToFileSystem`, no
  `ProtectKeysWith`) sits unencrypted in the data region too - antiforgery/cookies, not
  credentials, same co-location. Fold in only if the KMS path ever happens.

### Federated Authentication & Identity

**Status: implemented on `feature/identity-full` (PR #1038 - open, mergeable, not yet reviewed).**
None of it is on `dev` yet, so this section stays until that lands. Covered by the branch: SAML 2.0 SP
(incl. SP- vs IdP-initiated flows), OIDC/OAuth 2.0 RP with dynamic scheme registration and IdP presets,
JIT provisioning with claims/role mapping, break-glass local auth, MFA (TOTP + recovery codes),
passkeys, audit logging - plus RBAC that is shipped rather than merely "prepared" (Viewer / Operator /
Site Admin with per-site memberships and groups, enforced by declarative service-layer gates).

The one bullet this section carried that was NOT built, and should not be revived as written:

- **Token model upgrade.** The old plan was access_token + refresh_token with rotation and DB-backed
  refresh-token families. The branch went the other way for interactive auth: an ASP.NET Identity
  **cookie** session with revocation driven by the security stamp, checked per request rather than by
  token expiry. That removes the need for refresh-token machinery on the interactive path entirely.
  JWTs remain only on the non-interactive API endpoints - if anything is left to do here, it is scoping
  lifetime/rotation for *those* API tokens, not rebuilding an OIDC token model for the UI.

### Site Lifecycle Management

Shipped: `SetSiteEnabledAsync` deactivates a site (stops collection, drops the console, hides it from
the switcher, keeps all data), and `DeleteSiteAsync` removes the `Sites` + `SiteAgents` rows and the
per-site DB directory behind a confirmation dialog, freeing the license seat.

- [ ] **InfluxDB buckets are not deleted** on site removal - `<slug>-*` buckets survive and must be
  removed by hand in InfluxDB. The confirmation dialog says so explicitly, so this is a deliberate
  gap rather than a bug; close it only if operators actually trip over the leftovers.

### Agent LAN IP detection on Docker hosts (test / harden)
The agent detects its LAN IP once at startup via `NetworkUtilities.DetectLocalIpFromInterfaces()`
(physical Ethernet > WiFi > other, skipping loopback and virtual/container NICs by name) and reports
it on enrollment, every heartbeat, and the tunnel hello; the server stores it on `SiteAgents.LanIp`.
That IP is what site clients are pointed at for LAN speed tests and what path analysis uses as the
agent's trace source (LAN and agent-run WAN tests).

The override shipped: `NO_AGENT_LAN_IP` wins over detection, documented in the agent README for
Docker bridge mode and multi-NIC hosts. What is left is verification, not code:

- **Host network mode:** container sees the real host NICs - should work as-is, but untested.
- **Bridge mode:** inside the container, `eth0` is a plain-looking Ethernet NIC with the
  container-internal address (e.g. 172.17.x) - the docker/veth skip-list only matches host-side
  names, so detection reports an IP that is unreachable from the site LAN and absent from the site's
  UniFi topology. Speed-test targets and traces break silently unless the override is set.
- [ ] Run the test matrix: native (works today), Docker host mode, Docker bridge with override.

### Main site agent: speed test capability consistency (only if a user asks)
`site.agent_covers_collection` moves probes, upstream path discovery, SNMP and device reachability
to a main site's agent, and the client speed test target follows it. Speed test *hosting* is a
separate capability: the agent announces `serves_speed_test` in its tunnel hello (driven by
`lanSpeedTest` in `agent.json`), and `SiteSpeedTestTargetResolver` takes it at its word, so an agent
that hosts no speed test sends Client Speed Test, Client Dashboard and Client WAN Test back to the
central server while everything else stays on the agent.

WAN Speed Test does not follow that flag. `RunsOnAgent` keys on coverage alone, and the agent's WAN
test is `uwnspeedtest` - a different binary from the nginx LAN page, so declining to host the page
says nothing about whether it can run a WAN test. The result is that WAN tests can only be moved
back to the server by turning coverage off entirely, which drags probes and SNMP back with them.

Deliberately left alone: the split is coherent (hosting a page for site clients is not the same
capability as running a test from the site), and nobody has asked for the two to move together. If
someone wants a covered main site whose speed tests all stay on the server:

- [ ] Decide whether WAN Speed Test should consult `ServesSpeedTest`, or whether the agent needs a
      second capability flag for "can run a WAN test" so the two stay independently addressable.
- [ ] Tear down `netopt-speedtest-nginx` when `lanSpeedTest` goes false. The unit is `WantedBy` the
      agent service and is not driven by `agent.json`, so it relights on every agent start and keeps
      serving port 24443 with no relay behind it: the page loads and results silently never post.
      Today that needs a manual `systemctl disable --now`.
- [ ] Surface it in the UI. Both switches are file-level today (`agent.json`, plus the installer's
      `--lan-speed-test`), which is fine for an operator and not something to document to users.

## Distribution

### ISO/OVA Image for MSP Deployment
- Create distributable ISO and/or OVA image for MSP users
- Pre-configured Linux appliance with Network Optimizer installed
- Easy deployment to customer sites without Docker expertise
- Consider: Ubuntu Server base, auto-updates, web-based initial setup

## UI / Tooltips

### Audit Clickable Tooltips for `data-tooltip-hover-only`
- `data-tooltip-hover-only` is the unified attribute for clickable elements - sets `trigger: 'mouseenter'` and `touch: false` so tapping on mobile just performs the action
- Buttons (`<button>`) get this behavior automatically via tag detection in App.razor
- Non-button clickables (`<a>`, `<div>` with `@onclick`, etc.) need the explicit `data-tooltip-hover-only` attribute
- Audit remaining clickable elements across the app and add the attribute where missing

## General

### 3D Map - Speed Test Path Overlay Rework
- Toggle hidden from overlay controls until the feature is useful
- Code exists in `lan-flow-map.js` (`_loadInitialSpeedTests`, `_renderSpeedTestOverlay`)
- The `// TODO` next to the commented-out `overlayDefs` entry points at
  `research/monitoring/3d-map-overlays-TODO.md`, which does not exist in the repo - repoint that
  comment here (or restore the file) when this is picked up
- Needs: visual design pass (hard to distinguish from traffic flow), results on hover/click, active test animation, filter by test type, time-based filtering
- Consider making it a temporary overlay during/after a test rather than a persistent toggle

### Minify Custom JS Resources
- `lan-flow-map.js`, `latency-charts.js`, `device-health-charts.js` are served unminified
- Add a build step (terser or esbuild) to produce `.min.js` variants and reference those in production
- Matches the pattern used for OpenSpeedTest (`app-2.5.4.js` → `app-2.5.4.min.js`)

### Fix Area Chart Gradient Direction for Negative Values
- ApexCharts gradient fill always renders opaque-to-transparent top-to-bottom
- For positive values (CM power, temperature), the dense color is at the line fading down toward zero - correct
- For negative values (ONT/SFP RX power at -19.8 dBm), the dense color is at zero fading down toward the line - visually inverted
- The opacity gradient should be densest at the line regardless of sign
- Requires patching the SVG gradient generation in our forked `tvancott42/Blazor-ApexCharts`
- `fillTo: 'end'` doesn't solve this - it changes the fill region, not the gradient direction
- Affects: ONT RX power chart, SFP RX power chart, cellular RSRP chart (all negative dBm values)

### Extract Shared Time-Range Chart Controls
- `latency-charts.js` and `device-health-charts.js` duplicate the same time-range control logic (presets, shift arrows, custom range popover, filter badges, poll interval scaling)
- Extract into a shared JS module so all chart sets reuse one implementation
- Both files have a TODO marking this

### Refactor Program.cs - Finish Breaking Up the Inline Endpoints
- **Done so far:** schedule executor registrations live in `ScheduleExecutorRegistration.cs`, and 17
  endpoint files exist under `Endpoints/` (alerts, speed test, the chart sets, ISP health, LAN flow map,
  site agent, SNMP, port stats, flaky target, monitoring investigate).
- **Still open:** `Program.cs` is ~2300 lines with roughly 47 inline `app.Map*` registrations that carry
  business logic in the handler - mainly auth, UPnP notes, AP locations, and the floor-plan CRUD block.
  Move those into endpoint groups the same way, and push handler logic into services.
- **Priority:** Medium - not blocking but makes maintenance harder as the app grows

### Refactor DnsSecurityAnalyzer.AnalyzeAsync() Parameter Hell
- **Issue:** `DnsSecurityAnalyzer.AnalyzeAsync()` now takes **14** parameters (was 7, then 12; it has
  since gained `networkConfigs` and `trustedDnsRedirectTargets`, and both belong in the record sketch
  below). Plus 4 convenience overloads. The signature as of the last count:
  ```csharp
  public async Task<DnsSecurityResult> AnalyzeAsync(
      JsonElement? settingsData, List<FirewallRule>? firewallRules,
      List<SwitchInfo>? switches, List<NetworkInfo>? networks,
      JsonElement? deviceData, int? customDnsManagementPort,
      JsonElement? natRulesData, List<int>? dnatExcludedVlanIds,
      string? externalZoneId, FirewallZoneLookup? zoneLookup,
      Dictionary<string, UniFiFirewallGroup>? firewallGroups,
      string? customDnsManagementUrl)
  ```
- **Problems:**
  - Easy to pass arguments in wrong order (all are nullable)
  - Tests are verbose with many `null` placeholders
  - Adding new parameters requires updating all call sites and overloads
  - The overload chain is getting unwieldy, and the parameter count is still growing
- **Proposed fix:** Create `DnsAnalysisRequest` record/class:
  ```csharp
  public record DnsAnalysisRequest
  {
      public JsonElement? SettingsData { get; init; }
      public List<FirewallRule>? FirewallRules { get; init; }
      public List<SwitchInfo>? Switches { get; init; }
      public List<NetworkInfo>? Networks { get; init; }
      public JsonElement? DeviceData { get; init; }
      public int? CustomDnsManagementPort { get; init; }
      public string? CustomDnsManagementUrl { get; init; }
      public JsonElement? NatRulesData { get; init; }
      public List<int>? DnatExcludedVlanIds { get; init; }
      public string? ExternalZoneId { get; init; }
      public FirewallZoneLookup? ZoneLookup { get; init; }
      public Dictionary<string, UniFiFirewallGroup>? FirewallGroups { get; init; }
  }
  ```
- **Benefits:**
  - Named parameters make call sites self-documenting
  - Adding new fields doesn't break existing callers
  - Eliminates the 5 overloads - just one method with a request object
  - Test setup becomes clearer
- **Also applies to:** Other analyzers with similar parameter patterns

### Consolidate DNAT Rule Coverage Type Strings
- **Issue:** `DnatRuleInfo.CoverageType` uses magic strings: `"network"`, `"subnet"`, `"single_ip"`, `"inverted_address"`, `"interface"`
- **Current usage:** Set in `ParseSourceFilter()`, consumed in `Analyze()` switch statement
- **Fix:** Replace with an enum `DnatCoverageType` for type safety and discoverability
- **Scope:** `DnatDnsAnalyzer.cs` only - fully self-contained

### Relocate DefaultSiteSlug Constant to a Shared Project
- **Issue:** `SiteManagementService.DefaultSiteSlug` (`"main"`) lives in `NetworkOptimizer.Web`, but lower-level projects that reference only `NetworkOptimizer.Core` need the same value. `AlertProcessingService.ResolveSourceUrl` (`NetworkOptimizer.Alerts`) can't reference it - the dependency points the wrong way - so it hardcodes the literal `"main"` with a comment.
- **Fix:** Move the constant down into a shared low-level home (`NetworkOptimizer.Core`), then repoint every reference (`SiteManagementService`, `SiteContextService`, `AlertProcessingService`, and any others).
- **Scope:** Multiple projects; small but touches DI/reference sites, so needs a rebuild + test pass. Not urgent - the literal is correct as long as the constant stays `"main"`.

### ThirdPartyDnsDetector Probe Method Duplication
- **Issue:** Two overloads of `TryProbePiholeEndpointAsync` and `TryProbeAdGuardHomeEndpointAsync` - one takes a full URL, one takes IP+port+scheme. The logic is nearly identical.
- **Fix:** Unify into a single method that takes a URL string. The IP+port caller can construct the URL before calling.
- **Scope:** `ThirdPartyDnsDetector.cs` only

### Consolidate udm-boot handling on IUdmBootService

- **Context:** udm-boot install was extracted into a shared `IUdmBootService` / `UdmBootService` (`src/NetworkOptimizer.Web/Services/Ssh/UdmBootService.cs`) when the Monitoring Interfaces feature landed. `SqmDeploymentService.InstallUdmBootAsync` now delegates to it, but several other call sites still hand-roll udm-boot logic and should adopt the shared service. Each is marked with a `TODO` comment in code.
- **Sites to migrate (do not duplicate the systemd unit or the inline check):**
  - `PerfTweaksDeploymentService.InstallUdmBootAsync` - currently routes through `SqmDeploymentService.InstallUdmBootAsync`; depend on `IUdmBootService` directly to drop the PerfTweaks -> SQM -> UdmBootService chain.
  - `PerfTweaksDeploymentService.CheckAllStatusAsync` - inline `test -f /etc/systemd/system/udm-boot.service` check; use `IUdmBootService.IsInstalledAsync()`.
  - `SqmDeploymentService.CheckDeploymentStatusAsync` - inline udm-boot test; use `IUdmBootService.IsInstalledAsync()`.
  - `WanSteerDeploymentService` status check - inline udm-boot test; use `IUdmBootService.IsInstalledAsync()`.
- **Note:** these inline checks are batched into larger delimited SSH status commands, so migrating them means either issuing a small extra call or having `IUdmBootService` expose the raw check fragment. Weigh the extra round-trip against the dedup; not blocking.

### Rename ISpeedTestRepository to IGatewayRepository
- **Issue:** `ISpeedTestRepository` is a misleading name - it handles Gateway SSH settings, iperf3 results, AND SQM WAN configuration
- **Current location:** `src/NetworkOptimizer.Storage/Interfaces/ISpeedTestRepository.cs`
- **Proposed name:** `IGatewayRepository` (all methods are gateway-related)
- **Refactor scope:**
  - Rename interface and implementation (`SpeedTestRepository.cs`)
  - Update all DI registrations in `Program.cs`
  - Update all injection sites across the codebase
  - Consider if gateway SSH settings should be a separate repository

### Database Normalization Review
- Review SQLite schema for proper normal form (1NF, 2NF, 3NF)
- Ensure proper use of primary keys, foreign keys, and indices
- Audit table relationships and consider splitting denormalized data
- JSON columns are intentional for flexible nested data (e.g., PathAnalysisJson, RawJson)
- Consider: Separate Clients table with FK references instead of storing ClientMac/ClientName inline

### Normalize Environment Variable Handling
- Current: Mixed patterns for reading configuration
  - Direct env var reads: `HOST_IP`, `APP_PASSWORD`, `HOST_NAME` (via `Environment.GetEnvironmentVariable()`)
  - .NET configuration: `Iperf3Server:Enabled` (via `IConfiguration`, requires `Iperf3Server__Enabled` env var format)
- Problem: Inconsistent for native deployments (Docker translates `IPERF3_SERVER_ENABLED` → `Iperf3Server__Enabled`)
- Options:
  1. Route everything through .NET configuration (use `__` notation everywhere)
  2. Route everything through direct env var reads (simpler for native)
  3. Support both patterns in app (check env var first, fall back to config)
- Low priority but would improve consistency

### Debounce UI-Triggered Modem Polls
- **Issue:** Multiple rapid modem polls can occur when navigating between pages
- **Cause:** `CellularStatsPanel` triggers `PollModemAsync` on render when no cached stats exist; multiple component instances can poll simultaneously before any completes
- **Observed:** 4-5 polls within 4 seconds when navigating dashboard → settings
- **Fix:** Add debounce or lock around UI-triggered polls in `CellularModemService`
- **Severity:** Low (causes extra SSH traffic but no errors)
- **Nothing guards this path today:** the `_isPolling` flag only covers the timer-driven
  `PollAllModemsAsync`. The UI path - `PollModemAsync`, called directly from `CellularStatsPanel` -
  has neither a lock nor a debounce, so the observed 4-5 polls in 4 seconds are unguarded.

### Shared IP-to-Client-Name Resolver
- Threat Dashboard resolves local IPs to UniFi client names inline (fetches clients, builds IP→name dict)
- Currently cached for 30 seconds (static across Blazor circuits) to avoid hammering the API
- **Note:** Real-time features (e.g., live threat feed, active monitoring) will need to invalidate/refresh the cache before using it, since device IPs can change via DHCP
- Other pages that display IPs could benefit from the same lookup:
  - Security Audit (firewall rules referencing IPs)
  - Config Optimizer (device references)
- Refactor into a shared service (e.g., `IClientNameResolver` in `NetworkOptimizer.Web/Services/`)
- Shared service should expose `InvalidateCache()` for real-time consumers

### Uniform Date/Time Formatting in UI
- Audit all date/time displays across the UI for consistency
- Standardize format (e.g., "Jan 4, 2026 3:45 PM" vs "2026-01-04 15:45:00")
- Consider user timezone preferences
- Affected areas: Speed test results, audit history, device last seen, logs

## UniFi Device Classification (v2 API)

The UniFi v2 device API (`/proxy/network/v2/api/site/{site}/device`) returns multiple device arrays for improved device classification and VLAN security auditing.

### Device Arrays from v2 API

| Array | Description | VLAN Recommendation | Status |
|-------|-------------|---------------------|--------|
| `network_devices` | APs, Switches, Gateways | Management VLAN | Existing |
| `protect_devices` | Cameras, Doorbells, NVRs, Sensors | Security VLAN | Done |
| `drive_devices` | UNAS | n/a | Done (used to exclude Drive devices from camera detection) |
| `access_devices` | Door locks, readers | Security VLAN | Deserialized, unused |
| `connect_devices` | EV chargers, other Connect devices | IoT VLAN | Deserialized, unused |
| `led_devices` | LED controllers, lighting | IoT VLAN | Deserialized, unused |
| `talk_devices` | Intercoms, phones | IoT/VoIP VLAN | Not even a DTO property |

`UniFiProtectDeviceResponse` already deserializes access/connect/led into generic lists that nothing
reads, so Phases 2/3/5 start at classification, not parsing. `talk_devices` is one step further back.

### Protect Infrastructure Devices (SuperLink, Sensors, Chimes)
- Currently excluded from VLAN placement checks: SuperLink Hub, Sensors, Chimes, Bridges
- These are wired (SuperLink) or wireless Protect devices that aren't cameras/doorbells/NVRs
- VLAN placement is ambiguous - depends on user's network design:
  - If Protect Console is on Security VLAN, these should follow
  - If Protect Console is on Management VLAN, SuperLink could go either way
  - Sensors and chimes carry security-sensitive data (motion, door open/close) - some users consider this Security VLAN worthy, others treat them as IoT
- Current `RequiresSecurityVlan` only covers the unambiguous set: cameras, doorbells, NVRs, AI Key
- Options:
  1. Add these to `RequiresSecurityVlan` and always recommend Security VLAN
  2. Tie recommendation to where the Protect Console itself lives (if Console is on Security, recommend Security for all Protect devices)
  3. Leave it to the Manual Network Purpose Override feature (let users decide)
- Likely best approach: option 2 (follow the Console) with option 3 as fallback

### Phase 2: Access Devices (Door Access)
- [ ] Parse `access_devices` array
- [ ] Identify door locks, card readers, intercoms
- [ ] Map to `ClientDeviceCategory.SmartLock` or new `AccessControl` category
- [ ] Recommend Security VLAN placement

### Phase 3: Connect Devices (EV Chargers, etc.)
- [ ] Parse `connect_devices` array
- [ ] Identify EV chargers, power devices
- [ ] Map to `ClientDeviceCategory.SmartPlug` or new `EVCharger` category
- [ ] Recommend IoT VLAN placement

### Phase 4: Talk Devices (Intercoms/Phones)
- [ ] Parse `talk_devices` array
- [ ] Identify intercoms, VoIP phones
- [ ] Map to `ClientDeviceCategory.VoIP` or `SmartSpeaker`
- [ ] Consider VoIP VLAN vs IoT VLAN recommendation

### Phase 5: LED Devices
- [ ] Parse `led_devices` array
- [ ] Identify LED controllers, smart lighting
- [ ] Map to `ClientDeviceCategory.SmartLighting`
- [ ] Recommend IoT VLAN placement

**Note:** The v2 API is only available on UniFi OS controllers (UDM, UCG, etc.). Device classification from the controller API is 100% confidence since the controller knows its own devices.

## Standalone Controller Support

### Legacy Network Server API paths: test coverage

The non-proxied `/api/s/{site}/...` root belongs to the **legacy UniFi Network Server** only. UniFi OS
Server and the gateway consoles (UDM, UCG) all go through `/proxy/network/...`, so this branch is
narrower than "standalone controller" suggests.

| Controller Type | API Path Pattern |
|-----------------|------------------|
| UniFi OS (UDM / UCG / UniFi OS Server) | `https://<ip>/proxy/network/api/s/{site}/stat/sta` |
| Legacy Network Server | `https://<ip>/api/s/{site}/stat/sta` |

Detection is implemented and has worked in practice: `DetectLoginType` probes `GET /login`,
`DetectControllerType` probes the proxied sysinfo endpoint, `BuildApiPath` / `BuildV2ApiPath` emit both
shapes, and legacy login goes to `/api/login`. It has been exercised in lab testing and some users run
it, but not regularly, and Ubiquiti keeps steering people off the legacy server - so this is low
priority and unlikely to grow.

- [ ] Add test coverage for the path and login detection, so the legacy branch cannot rot silently
  between releases that nobody exercises it in.

## Channel Recommendation: Learn From Tried Configs (Outcome History)

Track the channel combinations the user has actually run over time and the util/interference that
resulted, then weight recommendations toward combos that measurably performed better - a real
feedback loop instead of inference. UniFi's own metrics history is too short and only reflects the
current channel, so we need our own longer-term store of tried configs and their outcomes.

**Motivation:** The engine can produce confident false positives. Example (2.4 GHz, two co-located
APs): it scored a straight 1<->11 swap as ~17% better, but the operator who set these channels up
knows the swap is actually worse for util/interference. The "improvement" came almost entirely from:
- **Propagated stress** - each AP's score on the channel it would move TO is inferred from the
  *other* AP's measurement (halved by proximity), not measured. Circular.
- **Self-induced load treated as environmental** - a channel's high utilization largely reflects
  that AP's own clients, which move with it; the model assumes switching channels escapes it.
- **Thin external margins, no direct scan data** - the only location-specific neighbor signal was
  triangulated (logs showed "no scan channel data"), with tiny per-channel deltas.

**The storage and attribution layer shipped.** `ChannelOutcomeBucket` persists daily
util/interference/tx-retry per (AP, channel, width) with 365-day retention; the collection service
attributes each hourly sample to the combo that was actually live via channel-change events (with
width-provenance rules so an unprovable width is recorded as unknown rather than guessed); and
`MergeLongTermOutcomes` folds those measurements into `HistoricalStress` age-decayed on a 60-day
half-life, where measured data wins over inferred wherever both exist. One deliberate ordering rule
inside that: a resident sibling's LIVE read outranks stale own-outcome memory.

Known ceiling, following from applying being out of scope (see "Applying a channel plan to UniFi"):
the collector sees the resulting live channel and width, but nothing ties an observed state back to a
specific recommendation - so the loop cannot tell a followed recommendation from a change the user
made for their own reasons. Outcomes are still attributed correctly; only the "did our advice help"
question stays out of reach.

Remaining:

- [ ] Down-weight (or flag) recommendations whose predicted gain rests mostly on propagated stress.
  Propagated stress is already 50%-dampened, proximity-scaled, kept in its own field and excluded from
  every ground-truth consumer - but nothing detects or surfaces *this particular recommendation is
  mostly propagated inference*.
- [ ] Distinguish self-induced load from environmental interference. Much of this landed (cross-vantage
  scoring so an AP's own contaminated read is not used for its current channel, own-scan excluded from
  measured congestion, contention/utilization split with an own-load floor). The gap left: historical
  utilization of the current channel still includes the AP's own clients, so a move can still take
  partial credit for "escaping" traffic that would follow it, offset only by `UnknownChannelPenalty`.
  The 1<->11 example above is the acceptance test.

## Channel Recommendation: Spectrum-Scan UX (background / on-demand quick-scans)

We now read per-channel measured occupancy from UniFi's `stat/spectrum-scan/{mac}` and can trigger
scans via `cmd/devmgr` quick-scan (`UniFiApiClient.TriggerQuickScanAsync`). A quick-scan does NOT
disconnect clients (the association stays up - only a FULL scan drops clients), BUT it briefly steps
each radio off its channel, so real-time traffic (video calls, gaming, an active speed test) can
hiccup for a moment - verified live (an iperf3 run died mid-scan; Wi-Fi stayed connected). It's also
slow per band per AP, so we never run it inline with a recommendation. The recommender reads whatever
scan results are cached; a band/AP with no recent scan falls back to the neighbor-scan (external)
proxy. UX should be honest: "clients stay connected, but real-time traffic may briefly hiccup -
best run during low-usage times" (NOT "won't disrupt"). Mesh/wireless-uplink APs can't quick-scan at
all (controller refuses - it'd drop their uplink), so exclude them from gap prompts.

Shared piece to build once: a "trigger -> poll `quick_scan_state.in_progress` until done -> read
results -> re-run rec" helper. Expose the trigger through `IWiFiDataProvider` (it currently lives
only on `UniFiApiClient`).

The gap-aware prompt (Win 1) and the shared trigger -> poll -> re-run helper both shipped, as did most
of the staleness work - in a better form than originally specified. The real `spectrum_table_time` is
propagated into a dedicated `ChannelScanResult.SpectrumTableTime` (rather than overwriting `ScanTime`
with it), and staleness drives its own "stale scan data" banner with a Re-scan button, gated on the
stale reading actually being material and uncorroborated. **Do not re-implement the original
"older than ~7 days = treat as a gap" design** - the materiality-gated banner deliberately replaced it.

- [ ] **Win 2 remainder.** Age never promotes an (AP, band) to a plain gap - `GetSpectrumScanGapsAsync`
  still filters on `Channels.Count == 0` only. And there is no manual "Refresh measurements" button for
  *all* APs; only the gap-targeted and stale-targeted buttons exist. Both are cheap: reuse
  `RunQuickScansAsync`.
- [ ] **Win 3 (scheduled off-peak sweep)** - background job that sweeps quick-scans across APs/bands
  during low-utilization hours, determined from the 1d/7d historic stress we already compute. Stagger
  ONE band at a time PER AP (parallel across APs - a radio scans one band at a time); never take the
  whole site's airtime off-channel at once. Spectrum cache stays fresh, recommendations always
  well-grounded, zero user action.
  - **Scan-hardware-aware scheduling (use `HasDedicatedScanRadio`):** APs with a dedicated all-band
    scan radio (verified: phy reports Band 1+2+4) scan with ZERO disruption to clients or mesh
    uplinks - so scan those freely/immediately, anytime. Only serving-radio APs (no scan radio) need
    the off-peak window. Mesh parents WITHOUT a scan radio are the most disruptive (scanning the uplink
    band drops children) - schedule those most conservatively or skip their uplink band.
- [ ] **Deep-analysis mode (premium, user-initiated)** - trigger fresh scans on ALL radios, wait,
  then recommend on fully-measured data. Best accuracy, slow (minutes); distinct from the fast
  everyday rec.
- [ ] **Large-deployment UX / verbiage (> ~8 APs).** The gap prompt, manual refresh, and rec copy are
  tuned for small sites. At higher AP counts revisit:
  - *Scan flow:* running quick scans across many APs is slow even parallel-across-APs, and it currently
    blocks the Blazor circuit with a spinner. Background it with real progress (per-AP/percent), or
    batch and let the rec refresh incrementally.
  - *Verbiage/counts:* "N radios haven't been scanned" and the gap banner read fine for 3-5 radios but
    get unwieldy at 20+; consider summarizing ("most radios", grouped by AP, collapsible detail).
  - *Rec table density* and "How This Works" at scale.
  - *Perf (profile, don't assume):* the per-AP fallback recomputes `ScoreAssignment` per candidate and
    `ScanReadingForScoring` loops siblings for current-channel reads - both negligible at small n but
    worth checking on large sites before they bite.

## Retention: alerts and audit events are never pruned

The Application Settings card carried an "Alert Retention (days)" field wired to a `SaveAppSettings`
stub that did nothing, so the value never persisted. The field and its dead Save button were removed
rather than made to persist a number nothing reads - storing it would have turned a visibly broken
control into an invisibly broken one.

Nothing in the solution consumes a retention value today:
- `IAlertRepository` has no delete/prune method at all - alert history grows without bound.
- `AlertEngine.ClearOldAlerts(TimeSpan)` exists in Monitoring with **zero callers**.
- `IAuditRepository.DeleteOldAuditsAsync` exists with **zero callers**, so the audit retention
  design doc 05 specifies (365 days + a row cap, with `audit.pruned` itself audited) is unimplemented.

The real fix, as one piece of work:
- [ ] Add pruning to `IAlertRepository` and run it from a background service, mirroring how the
  monitoring collectors are hosted. Per-site DBs mean the job has to walk every site, not just main.
- [ ] Implement audit pruning to doc 05: time-based default 365d plus a row-count cap, emitting an
  `audit.pruned` event with count and range.
- [ ] Reinstate the settings UI once something consumes the values, saving through the gated
  `ISystemSettingsAdmin` so the change is Admin-only and audited like every other settings write.


## Identity review leftovers

Three findings from the security review of this branch, accepted rather than fixed. Each was traced
to a concrete failure and then judged not worth the change it would take.

### Two admins disabling each other at the same instant

`SetEnabledAsync` counts the enabled admins, then writes. The count now comes from a fresh
no-tracking context (it used to read stale tracked entities, which was the real bug), but the check
and the write are still two steps: two admins disabling each other within the same instant can both
see two admins, both pass, and leave none.

Not fixed because the two halves go through different contexts - the count through the DbContext
factory, the write through `UserManager` on the scoped one. Making them atomic means either a
transaction spanning both, or dropping to a conditional `ExecuteUpdateAsync`, which bypasses
`UserManager` and loses the concurrency stamp, the security-stamp rotation and the revocation notify
that the same method also performs.

The outcome is also no longer terminal: break-glass re-enables the built-in `admin` on a
`NETOPT_RECOVERY=1` boot, so the worst case is a restart with an env var rather than a rebuilt
install. Revisit if the identity tables ever move behind a repository that owns both operations.

### Encrypted SAML assertions are not implemented

`FederationProvider.WantAssertionsEncrypted` and `SamlDecryptionCertProtected` exist as columns and
nothing else - no UI, no reader, no decryption certificate wired into the SAML configuration. Nothing
misleading is on screen, because neither is exposed; the gap is that design doc 03 lists encrypted
assertions among the SAML features as though they ship.

- [ ] Either implement them (decryption certificate upload, wire it into `Saml2Configuration`, and
  enforce the flag by refusing an unencrypted assertion when it is set), or drop the columns and
  correct doc 03. Do not enforce the flag alone: with no decryption certificate the library cannot
  read an encrypted assertion, so enforcement without the rest of the feature fails every login.

### SecureContext reads a forwarded header directly

`SecureContext.IsSecure` reads `X-Forwarded-Proto` off the request. `CanonicalOrigin` documents why
it deliberately does NOT do that - the header is attacker-supplied unless `UseForwardedHeaders` plus
`TRUSTED_PROXIES` has already rewritten `Request.Scheme`.

Low, because the only consumer decides whether to offer the passkey UI, and the browser independently
refuses a WebAuthn ceremony in a non-secure context - so spoofing the header buys an attacker a button
that then fails. It becomes real the moment `IsSecure` is used for anything the browser does not
separately gate.

- [ ] Resolve the scheme through `context.Request.Scheme` the way `CanonicalOrigin` does.

### Guided tours are not aware of who is taking them

Now that roles and per-site access exist, two assumptions in the tour engine no longer hold. Neither
is a regression - the engine predates RBAC - but both are wrong for any install with more than the
one built-in account.

**Steps are not gated by role or scope.** `TourService` consults `SiteContextService` for the
`gateway-ssh` / `multi-site` / `has-agent` predicates and for rewriting `?site=`, and that is all the
context it has. There is no role check and no default-site check, so a step anchored on Settings -
Identity or Settings - Audit Log is offered to a Viewer, and to a Site Admin standing on a non-default
site, who cannot reach either. They land on a page that refuses them, mid-tour.

**Tour state is install-wide, not per user.** `TourStateService` reads and writes the single
`AdminSettings` row (`TourOffers`, plus `LastSeenAppVersion` / `FirstSeenVersion` stamped by
`TourStartupService`). So the first account to be offered a tour consumes it for everyone, one user's
Later defers it for the whole install, and a new user added later never sees anything.

- [ ] Add role/scope predicates so a step can declare what it needs (`global-admin`, `site-admin`,
  `default-site` are the ones the current candidate steps want) and resolve them from `ICallerContext`
  the way the existing predicates resolve from site state. Until then, mark any Admin-only or
  default-site-only step `"optional": true` so a missing target skips rather than breaks.
- [ ] Move the offer/defer state per user, keeping the install-level version stamps where they are -
  `FirstSeenVersion` is a fact about the install, but "has this person been offered 2.5.0" is a fact
  about the person.
