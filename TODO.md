# Network Optimizer - TODO / Future Enhancements

## LAN Speed Test

### Path Analysis Enhancements
- ✅ ~~Direction-aware bottleneck calculation~~ (done - `GetDirectionalEfficiency()` in PathAnalysisResult, separate TX/RX bottleneck in NetworkPathAnalyzer)
- More gateway models in routing limits table as we gather data
- Threshold tuning based on real-world data collection

### ✅ ~~Scheduled LAN Speed Test~~ (done - Alerts & Scheduling feature)

### ✅ ~~Scheduled WAN Speed Test~~ (done - Alerts & Scheduling feature)

## Alerts & Scheduling

### ✅ ~~LAN Speed Test Schedule: UniFi Device Targets~~ (done)

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
- **Co-channel interference severity scaling:** Reduce urgency/severity of co-channel interference warnings as AP count increases. With many APs in a dense deployment, some co-channel overlap is unavoidable and expected. Current warnings may be too aggressive for larger deployments.
- **MLO per-AP detection:** Check MLO status per-AP based on which SSIDs each AP broadcasts (via vap_table), not just global WLAN config. An AP only has MLO impact if it broadcasts an MLO-enabled SSID.

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

## General

### Refactor Program.cs - Extract Business Logic and Break Up API Sets
- **Issue:** `Program.cs` has grown into a monolith with schedule executor implementations, API endpoint registrations, and business logic all inline
- **Goal:** Clean separation of concerns:
  - Extract schedule executor registrations into a dedicated class (e.g., `ScheduleExecutorSetup.cs`)
  - Break API endpoints into logical groups using minimal API route groups or extension methods (e.g., `SpeedTestEndpoints.cs`, `AuditEndpoints.cs`, `ThreatEndpoints.cs`)
  - Move inline business logic out of endpoint handlers into services
- **Priority:** Medium - not blocking but makes maintenance harder as the app grows

### Refactor DnsSecurityAnalyzer.AnalyzeAsync() Parameter Hell
- **Issue:** `DnsSecurityAnalyzer.AnalyzeAsync()` takes 7 nullable parameters, making it error-prone:
  ```csharp
  public async Task<DnsSecurityResult> AnalyzeAsync(
      JsonElement? settingsData,
      JsonElement? firewallData,
      List<SwitchInfo>? switches,
      List<NetworkInfo>? networks,
      JsonElement? deviceData,
      int? customPiholePort,
      JsonElement? natRulesData)
  ```
- **Problems:**
  - Easy to pass arguments in wrong order (all are nullable)
  - Tests are verbose with many `null` placeholders
  - Adding new parameters requires updating all call sites
- **Proposed fix:** Create `DnsAnalysisRequest` record/class:
  ```csharp
  public record DnsAnalysisRequest
  {
      public JsonElement? SettingsData { get; init; }
      public JsonElement? FirewallData { get; init; }
      public List<SwitchInfo>? Switches { get; init; }
      public List<NetworkInfo>? Networks { get; init; }
      public JsonElement? DeviceData { get; init; }
      public int? CustomPiholePort { get; init; }
      public JsonElement? NatRulesData { get; init; }
  }
  ```
- **Benefits:**
  - Named parameters make call sites self-documenting
  - Adding new fields doesn't break existing callers
  - Test setup becomes clearer
- **Also applies to:** Other analyzers with similar parameter patterns

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
