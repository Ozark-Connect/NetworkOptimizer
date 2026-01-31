# Network Optimizer - TODO / Future Enhancements

## LAN Speed Test

### Path Analysis Enhancements
- Direction-aware bottleneck calculation: TX and RX rates can differ significantly on Wi-Fi (e.g., client may have 800 Mbps TX but 400 Mbps RX); path analysis should use direction-appropriate rate for separate from/to max speeds
- More gateway models in routing limits table as we gather data
- Threshold tuning based on real-world data collection

## Security Audit / PDF Report

### IPv6 Validation Limitations
- **Issue:** Need to identify where IPv6 gateway addresses are exposed in the API
- **Device API has full IPv6 data:** The `/stat/device` API's `network_table` includes complete IPv6 info when enabled:
  - `ipv6_interface_type`: "single_network" when enabled, "none" when disabled
  - `ipv6_single_network_interface`: Which WAN provides IPv6 (e.g., "WAN2")
  - `ipv6_link_local_address`: Gateway's link-local on this VLAN (e.g., "fe80::1e0b:8bff:fe14:9d75")
  - **`ipv6_subnets`**: Array with gateway's full IPv6 address/prefix (e.g., "2605:59c8:6b00:7c2c:1e0b:8bff:fe14:9d75/64")
- **WAN port IPv6 data** (also in device API):
  - `wan2.ipv6[]` - WAN interface's own IPv6 addresses (GUA + link-local)
  - `wan2.mac_table_ipv6[].ip` - Upstream ISP gateway IPv6 (e.g., "2605:59c8:6b00:7c2c::1")
  - `wan2.dns[]` - Can include IPv6 DNS servers
- **Current handling:**
  - IPv6 address format normalization works correctly (e.g., `2001:db8::1` equals `2001:0db8:0000:0000:0000:0000:0000:0001`)
  - IPv6 addresses in firewall groups are matched correctly
  - IPv6 DNAT rules may produce false "Invalid DNAT Translated IP" warnings if target is an IPv6 gateway (until device API parsing is added)
- **Future fix:** Parse `network_table[].ipv6_subnets` from device API to get IPv6 gateway addresses per network
- **Priority:** Low - IPv6 adoption on home/SMB LANs is minimal; most users don't have ISP IPv6 support
- **Reference:** See `sample-device-ipv6-response.txt` for full device API response with IPv6 enabled

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

### Printer/Scanner Audit Logic Consolidation
- **Issue:** Printer/Scanner VLAN placement logic is duplicated across multiple files
- **Current state:**
  - `IotVlanRule.cs` - wired devices, checks `isPrinter` inline
  - `WirelessIotVlanRule.cs` - wireless devices, checks `isPrinter` inline
  - `ConfigAuditEngine.cs` - offline wireless via `CheckOfflinePrinterPlacement()`
  - `VlanPlacementChecker.cs` - shared placement logic
- **Problems:**
  1. `isPrinter` check duplicated: `Category == Printer || Category == Scanner`
  2. Offline wired printers handled differently (via `HistoricalClient` in `IotVlanRule`) than offline wireless (separate method in `ConfigAuditEngine`)
  3. No dedicated `PrinterVlanRule` - piggybacks on IoT rules
- **Proposed fix:**
  - Add `IsPrinterOrScanner()` extension method to `ClientDeviceCategoryExtensions`
  - Consider dedicated `PrinterVlanRule` and `WirelessPrinterVlanRule` classes
  - Unify offline handling for wired/wireless printers

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

### AP / RF Performance Analysis (Design Session Needed)
- **Goal:** Provide RF performance insights beyond what UniFi Network offers natively
- **Prerequisite:** Reuse all device classification logic from Security Audit
- **Potential features to explore:**
  - Channel utilization analysis per AP
  - Client distribution balance across APs
  - Signal strength / SNR reporting per client
  - Interference detection (co-channel, adjacent channel)
  - Band steering effectiveness (are 5 GHz capable devices on 2.4 GHz?)
  - Roaming analysis (frequent roamers, sticky clients)
  - Airtime fairness issues (slow clients impacting fast clients)
  - AP placement recommendations based on client distribution
- **Data sources to investigate:**
  - UniFi API: What RF metrics are available?
  - SNMP: Additional metrics not exposed via API?
  - Client connection history: Roaming patterns
- **Design questions:**
  - What problems do users actually face that UniFi doesn't surface well?
  - What's actionable vs just informational?
  - How do we present RF data to non-experts?

## SQM (Smart Queue Management)

### Multi-WAN Support
- Support for 3rd, 4th, and N number of WAN connections
- Currently limited to two WAN connections
- Should dynamically detect and configure all available WAN interfaces

### GRE/PPP Tunnel Support
- Support for GRE and PPP tunnel connections (e.g., UniFi 5G modem, PPPoE)
- Currently specifically excluded from SQM configuration
- These tunnels should be treated as valid WAN interfaces for SQM purposes

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
