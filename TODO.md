# Network Optimizer - TODO / Future Enhancements

## LAN Speed Test

### Path Analysis Enhancements
- ✅ ~~Direction-aware bottleneck calculation~~ (done - `GetDirectionalEfficiency()` in PathAnalysisResult, separate TX/RX bottleneck in NetworkPathAnalyzer)
- More gateway models in routing limits table as we gather data
- Threshold tuning based on real-world data collection
- **Consistent wireless bottleneck attribution across test types:** LAN client speed tests show the bottleneck relative to the AP (e.g., "[AP] Back Yard (wireless)") while WAN client speed tests show it relative to the client (e.g., "[Phone] TJ iPhone (wireless)"). This is because WAN client paths reverse hops and swap ingress/egress, which flips the perspective. The wireless link is the same physical connection - both descriptions are technically correct but inconsistent. Investigate unifying to always name the AP side, since that's what users can control. Relevant code: `CalculateWanClientPathAsync` hop reversal/swap and `CalculateBottleneck` wireless link attribution.

### ✅ ~~Scheduled LAN Speed Test~~ (done - Alerts & Scheduling feature)

### ✅ ~~Scheduled WAN Speed Test~~ (done - Alerts & Scheduling feature)

## Alerts & Scheduling

### ✅ ~~LAN Speed Test Schedule: UniFi Device Targets~~ (done)

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
- These are Info severity - users who find them noisy can disable rule 13 in alert settings

## Security Audit / PDF Report

### Manual Network Purpose Override
- Allow users to manually set the purpose/classification of their Networks in Security Audit Settings
- Currently: Network purpose (IoT, Security, Guest, Management, etc.) is auto-detected from network name patterns
- Problem: Users with non-standard naming conventions get incorrect VLAN placement recommendations
- Implementation:
  - Add "Network Classifications" section to Security Audit Settings page
  - List all detected networks with current auto-detected purpose
  - Allow override via dropdown: Corporate, Home, IoT, Security, Guest, Management, Printer, Unknown
  - Store overrides in database (new table or extend existing settings)
  - VlanAnalyzer should check for user overrides before applying name-based detection
- Benefits:
  - Users with custom naming schemes can get accurate audits
  - Explicit classification removes ambiguity
  - Auto-detection still works as default for users who don't configure

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

### Third-Party DNS Firewall Rule Check
- When third-party DNS (Pi-hole, AdGuard, etc.) is detected on a network, check for a firewall rule blocking UDP 53 to the gateway
- Without this rule, clients could bypass third-party DNS by using the gateway directly
- Implementation: Look for firewall rules that DROP/REJECT UDP 53 from the affected VLANs to the gateway IP
- Severity: Recommended (not Critical, since some users intentionally allow fallback)
- **Status:** Awaiting user feedback on current third-party DNS feature before implementing

### ✅ ~~Printer/Scanner Audit Logic Consolidation~~ (done)
- Consolidated in `VlanPlacementChecker.CheckPrinterPlacement()`, called from `ConfigAuditEngine`

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

### Jumbo Frames Suggestion
- Suggest enabling Jumbo Frames as a global switching setting when high-speed devices are present
- Trigger: 2+ devices connected at 5 GbE or 10 GbE on access ports (not infrastructure uplinks)
- Rationale: Jumbo frames (9000 MTU) reduce CPU overhead and improve throughput for high-speed transfers
- Implementation:
  - Scan port_table for ports with speed >= 5000 Mbps
  - Exclude infrastructure ports (uplinks, trunks between switches)
  - If count >= 2, check if Jumbo Frames is already enabled globally
  - If not enabled, suggest enabling with explanation of benefits
- Caveats to mention in recommendation:
  - All devices in the path must support jumbo frames
  - Some IoT devices may not support non-standard MTU
  - WAN traffic still uses standard 1500 MTU
- Severity: Informational (performance optimization, not a problem)

### MTU Mismatch Detection
- Detect MTU mismatches along network paths that cause fragmentation or packet drops
- Implementation:
  - During path tracing, SSH into each hop (gateway, switches) to query interface MTU
  - Gateway: `ip link show <interface>` or parse `/sys/class/net/<iface>/mtu`
  - Switches: Check port MTU via SSH (UniFi switches support shell access)
  - Compare MTU values across the path - all devices should match
- Issues to detect:
  - Standard MTU (1500) mixed with Jumbo Frames (9000) in same path
  - Intermediate device with lower MTU than endpoints (causes fragmentation)
  - Jumbo Frames enabled on LAN but not on inter-switch uplinks
  - VPN/tunnel overhead not accounted for (e.g., WireGuard needs ~1420 MTU)
- Display: Show MTU at each hop in path analysis, flag mismatches
- Severity: Warning (mismatches cause performance degradation or silent drops)
- Prerequisite: Reuse SSH infrastructure from SQM/gateway speed tests

### WiFi Optimizer Enhancements
- **Channel recommendation: broaden search candidate generation (long-term, the real fix).** The exhaustive/greedy search prunes each AP to a small candidate channel set (e.g. ~2 channels/AP → only 8 assignments evaluated for a 4-AP 5 GHz site), so it can miss the globally optimal assignment - notably an "altruistic" move where relocating a still-healthy AP declutters a worse neighbor (e.g. move a fine AP off a shared 160 MHz block so a congested one stops sharing it). Today that gap is patched by an altruistic relocation pass in the per-AP fallback (`ChannelRecommendationService`, gated on site-wide score improvement), but the correct long-term fix is for the search itself to consider a richer candidate set per AP (e.g. all valid non-DFS blocks plus historically-good channels, with branch-and-bound pruning to keep the space tractable) so the global optimizer finds these moves directly and the fallback becomes a safety net rather than the source of the recommendation. When this lands, revisit whether the altruistic fallback pass is still needed.
- **Power & Coverage: per-band signal classification** - `GetSignalClass` and `GetSignalBucketClass` in PowerCoverageAnalysis.razor hardcode `RadioBand.Band5GHz` because they operate on aggregate values (avg signal, dBm bucket ranges) without per-client band context. Could classify each client by their actual band first, then aggregate the results. The signal distribution bar chart would need to either split by band or color each client's contribution by their band. Current behavior matches pre-band-aware thresholds so no regression, just a missed opportunity.
- **MLO per-AP detection:** Check MLO status per-AP based on which SSIDs each AP broadcasts (via vap_table), not just global WLAN config. An AP only has MLO impact if it broadcasts an MLO-enabled SSID.
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

#### Implemented Features (v1.x)
The following were implemented in the WiFi Optimizer feature:
- ✅ Channel utilization analysis per AP (Airtime Fairness tab)
- ✅ Client distribution balance across APs (AP Load Balance tab)
- ✅ Signal strength / SNR reporting per client (multiple components)
- ✅ Interference detection - co-channel, adjacent channel (Spectrum Analysis tab)
- ✅ Band steering effectiveness analysis (Band Steering tab)
- ✅ Roaming topology visualization (Connectivity Flow tab)
- ✅ Airtime fairness issues - legacy client impact (Airtime Fairness tab)
- ✅ Site health score with dimensional breakdown
- ✅ Power/coverage analysis with TX power recommendations

## SQM (Smart Queue Management)

### Retrofit Custom Cloudflare Speed Test Binary into Adaptive SQM
- Replace current WAN speed test approach in Adaptive SQM with the custom Cloudflare speed test binary
- The Cloudflare speed test provides more accurate and consistent WAN throughput measurements
- Integration points: SQM calibration, periodic re-calibration, manual speed test triggers
- Should use the same binary/approach as the standalone Cloudflare speed test projects

### Multi-WAN Support
- Support for 3rd, 4th, and N number of WAN connections
- Currently limited to two WAN connections
- Should dynamically detect and configure all available WAN interfaces

### GRE Tunnel Support (Cellular WAN)
- Support GRE tunnel connections from cellular modems (U5G-Max, U-LTE)
- These create GRE tunnels that should be treated as valid WAN interfaces for SQM
- ✅ ~~PPPoE support~~ (done - uses physical interface for lookup, tunnel interface for SQM)

## Monitoring

### Multi-WAN Support (ISP Health & NMS)
- ISP Health currently grades a single (primary) WAN. Several inputs are read globally rather than scoped to the WAN being scored:
  - **Upstream ancestry / `hopOrderKnown`** (`IspHealthService.ComputeAsync`): `UpstreamDiscoveries` are queried across all WANs. Rows carry `WanInterface`, and the tracer persists per-WAN, but the scorer reads them globally - so a second WAN's discovery data can flip the jitter-absolve gate (and the routes-through witnesses) for a WAN that has no ancestry of its own. Scope the discovery query and `hopOrderKnown` by `WanInterface`.
  - **Targets / series / rates** are likewise resolved for the primary WAN only; per-WAN scoring needs each WAN's own targets, latency series, throughput, and expected speeds.
- Plan: grade ISP Health per-WAN (one report per active WAN), keyed by `WanInterface` end-to-end, and surface a per-WAN selector in the UI. Until then, secondary WANs are not separately graded.
- **Relevant code:** `IspHealthService.ComputeAsync` (TODO marker at the discoveries query), `UpstreamTracerService.PersistHopOrderAsync` (already per-WAN), `MonitoringPathView` (already WAN-scoped).

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

### Monitoring Interfaces: Duplicate Reachable IP (DNAT + SNAT to alternate IP)

- **Context:** The Monitoring Interfaces feature (Setup tab) deploys a macvlan + `/32` route + SNAT on the gateway so the Network Optimizer server (a LAN client) and browsers can reach an ONT/modem management IP that sits behind the WAN. v1 runs two preflight gates before deploy and **bails with a report** if either fails:
  1. The target IP's subnet overlaps a known UniFi Network/VLAN (we already enumerate these via `UniFiNetworkConfig`) - monitoring that device this way isn't possible; user must renumber.
  2. The target IP is already pingable/reachable from the Network Optimizer server - either it already works (no plumbing needed) or it's a duplicate-IP collision we can't safely route to.
- **The deferred case:** Two devices share the same management IP on different WANs - e.g. a cable modem on `192.168.100.1` (WAN1) and a Starlink dish also on `192.168.100.1` (WAN2). A plain `/32` route is ambiguous; only one can win. To monitor both, we'd need to give each an **alternate virtual IP** the server targets, then **DNAT** that virtual IP to the real `192.168.100.1` pinned to the correct egress interface, plus the matching **SNAT** so replies return through the same macvlan.
  - Example shape: server polls `192.168.100.1` (modem) and `192.168.101.1` (alias for Starlink); gateway DNATs `192.168.101.1 -> 192.168.100.1 out <starlink-wan-macvlan>` and SNATs the LAN source to the per-WAN alias.
  - Needs: per-target alternate-IP allocation, DNAT+SNAT rule generation in the boot/watchdog script, idempotent teardown, and UI to surface the alternate IP the user should point monitoring at (since it's no longer the device's real IP).
- **v1 behavior:** detect the duplicate (preflight gate 2) and bail with a clear message explaining the collision and that alternate-IP DNAT support is planned. Do **not** silently deploy a route that hijacks the shared IP.
- **Relevant code (once built):** Monitoring Interfaces deployment service (preflight checks + boot script generation), `UniFiNetworkConfig` enumeration for the overlap gate, `NetworkUtilities.IsIpInSubnet` for overlap math.

## Multi-Tenant / Multi-Site Support

### Multi-Tenant Architecture
- Add multi-tenant support for single deployment serving multiple sites
- Current architecture: Local console access with local UniFi API
- Target architecture: Support tunneled access to multiple UniFi sites from one deployment
- Deployment models:
  - **Local (default):** Deploy instance at each site for direct LAN API access
  - **Centralized (optional):** Single deployment with VPN/tunnel access to multiple client networks
    - Requires unique IP structure per client (no overlapping subnets)
    - Relies on same local API access, just over tunnel instead of local LAN
- Use cases: MSPs managing multiple customer sites, enterprises with distributed locations
- Considerations:
  - Site/tenant isolation for data and configuration
  - Per-site authentication and API credentials
  - Tenant-aware database schema or separate databases per tenant
  - Site selector/switcher in UI
  - Aggregate dashboard views across sites (optional)

### Federated Authentication & Identity
- External IdP integration for enterprise/MSP deployments
- Protocol support:
  - **SAML 2.0:** Enterprise SSO (Okta, Azure AD, ADFS, etc.)
  - **OIDC/OAuth 2.0:** Modern identity providers (Auth0, Keycloak, Google Workspace)
- Architectural preparation for RBAC (Role-Based Access Control):
  - Abstract authentication layer to support pluggable identity sources
  - Claims/roles mapping from IdP to local permissions
  - Future: Granular permissions per site/tenant (view-only, operator, admin)
- **Token model upgrade** (prerequisite for multi-user):
  - Move from current single JWT to proper access_token + refresh_token OIDC model
  - Short-lived access tokens (1 hour) with long-lived refresh tokens
  - Applies to local auth as well, not just external IdP
  - Token rotation and revocation support
  - Secure refresh token storage (DB-backed with family tracking)
- Considerations:
  - SP-initiated vs IdP-initiated login flows
  - Just-in-time (JIT) user provisioning from IdP claims
  - Session management and token refresh across federated sessions
  - Fallback local auth for break-glass scenarios

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

### Refactor Program.cs - Extract Business Logic and Break Up API Sets
- **Issue:** `Program.cs` has grown into a monolith with schedule executor implementations, API endpoint registrations, and business logic all inline
- **Goal:** Clean separation of concerns:
  - Extract schedule executor registrations into a dedicated class (e.g., `ScheduleExecutorSetup.cs`)
  - Break API endpoints into logical groups using minimal API route groups or extension methods (e.g., `SpeedTestEndpoints.cs`, `AuditEndpoints.cs`, `ThreatEndpoints.cs`)
  - Move inline business logic out of endpoint handlers into services
- **Priority:** Medium - not blocking but makes maintenance harder as the app grows

### Refactor DnsSecurityAnalyzer.AnalyzeAsync() Parameter Hell
- **Issue:** `DnsSecurityAnalyzer.AnalyzeAsync()` now takes 12 parameters (was 7, grew during DNAT/firewall groups/URL work):
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
  Plus 5 convenience overloads that chain to it.
- **Problems:**
  - Easy to pass arguments in wrong order (all are nullable)
  - Tests are verbose with many `null` placeholders
  - Adding new parameters requires updating all call sites and overloads
  - The overload chain (lines 47-77) is getting unwieldy
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
- **Partial:** Basic `_isPolling` lock prevents concurrent polls, but no time-based debounce yet

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
| `access_devices` | Door locks, readers | Security VLAN | TODO |
| `connect_devices` | EV chargers, other Connect devices | IoT VLAN | TODO |
| `talk_devices` | Intercoms, phones | IoT/VoIP VLAN | TODO |
| `led_devices` | LED controllers, lighting | IoT VLAN | TODO |

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

### API Path Differences
Currently only tested with UniFi OS controllers (UDM, Cloud Gateway). Standalone controllers use different API paths:

| Controller Type | API Path Pattern |
|-----------------|------------------|
| UniFi OS (UDM/UCG) | `https://<ip>/proxy/network/api/s/{site}/stat/sta` |
| Standalone Controller | `https://<ip>/api/s/{site}/stat/sta` |

The app auto-detects controller type via login response, but needs testing with standalone controllers to verify:
- Path detection logic in `UniFiApiClient`
- All API endpoints work correctly
- Authentication flow differences (if any)

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

- [ ] Persist each observed (AP, channel, width) -> rolling util/interference/tx-retry outcome, long-term
- [ ] Attribute metrics to the combo that was actually live (key off channel-change events)
- [ ] When a candidate assignment matches a previously-tried combo, prefer measured outcome over inferred score
- [ ] Down-weight (or flag) recommendations whose predicted gain rests mostly on propagated stress
- [ ] Distinguish self-induced load from environmental interference - don't credit a move for escaping the AP's own traffic
