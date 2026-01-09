# Network Optimizer - TODO / Future Enhancements

## LAN Speed Test

### ~~Non-SSH Device Speed Testing~~ (DONE)
- ~~Current limitation: LAN speed tests require SSH access to target devices (UniFi equipment only)~~
- ~~Goal: Enable speed testing from any device (phones, tablets, laptops, IoT) without SSH~~
- DONE: Implemented both approaches:
  - Server-side iperf3 server with automatic result capture
  - OpenSpeedTest integration with branded instance and result correlation
  - Device identification via source IP matched to UniFi client list
  - Full path analysis integration for browser-based tests

### ~~Retransmit Analysis~~ (FIXED)
- ~~Flag high retransmit counts as a separate insight~~
- ~~Example: "High packet loss detected on return path" when retransmits are asymmetric~~
- ~~Observation: Front Yard AP test showed 3181 retransmits "to device" but 0 "from device"~~
- ~~This asymmetry on wireless mesh links makes sense (mesh uplink contention)~~
- FIXED: PathAnalysisResult.AnalyzeRetransmits() detects high/asymmetric retransmits and generates insights

### Path Analysis Enhancements
- Direction-aware bottleneck calculation: TX and RX rates can differ significantly on Wi-Fi (e.g., client may have 800 Mbps TX but 400 Mbps RX); path analysis should use direction-appropriate rate for separate from/to max speeds
- More gateway models in routing limits table as we gather data
- Threshold tuning based on real-world data collection

### Speed Test Map Auto-Display After Data Cleared
- **Bug:** Map doesn't auto-display when new results come in after all results were cleared
- **Affected pages:** Client Speed Test, LAN Speed Test (both have SpeedTestMap component)
- **Repro:** Clear all speed test results (manually or external), map hides, run new test, map stays hidden
- **Expected:** Map should auto-display when first result with location data arrives
- **Fix:** Reset map visibility state when results are cleared, or check on each poll if map should show

### Speed Test Map Time Range Filters
- Add time range filter to speed test map (default: last 30 days)
- Options: 7 days, 30 days, 90 days, 1 year, all time
- Currently shows up to 1000 most recent results regardless of age
- Allows focusing on recent coverage data vs historical trends

## Security Audit / PDF Report

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

### ~~Collapsible Audit Findings by Category~~ (DONE)
- ~~Collapse multiple findings of the same audit category into a single expandable row~~
- ~~Example: "10 IoT devices on wrong VLAN" instead of 10 separate rows~~
- ~~Click to expand and view all individual findings in that category~~
- ~~Benefits:~~
  - ~~Cleaner UI when many findings of same type exist~~
  - ~~Faster scanning of audit results~~
  - ~~Still allows drill-down to specific devices/issues~~
- ~~Affected components: AlertsList, Audit.razor~~
- DONE: Implemented collapsible category groups in Audit.razor with expand/collapse functionality

### ~~Excluded Features Still Affecting Score~~ (FIXED)
- ~~Features unchecked in the Security Audit analysis are still factoring into scoring~~
- ~~Expected behavior: Excluding a feature should not display results AND not affect the score~~
- ~~Current behavior: Excluded features still deduct/add scoring amounts~~
- ~~Fix: Scoring calculation should skip any checks for features that are disabled~~
- FIXED: Score now recalculated based only on enabled features in AuditService.CalculateFilteredScore()

### ~~Wireless Client Band Info~~ (FIXED)
- ~~Show WiFi band (2.4 GHz/5 GHz/6 GHz) in the Port column for wireless issues~~
- ~~Example: "on [AP] Tiny Home (5 GHz)" instead of just "on [AP] Tiny Home"~~
- ~~Data source: UniFi client response has radio/channel info~~
- FIXED: WirelessClientInfo.WifiBand computed from radio type, displayed in GetPortDisplay()

### ~~Port Audit - Down Ports with MAC Restriction~~ (FIXED)
- ~~Support VLAN placement analysis for ports that are currently down~~
- ~~Use the MAC restriction setting to identify the normally connected device~~
- ~~This allows auditing device placement even when the device is offline/disconnected~~
- ~~Currently: Down ports may be skipped or show no connected MAC for analysis~~
- FIXED: IotVlanRule and CameraVlanRule now analyze down ports with MAC restrictions using MAC OUI detection

## SQM (Smart Queue Management)

### Multi-WAN Support
- Support for 3rd, 4th, and N number of WAN connections
- Currently limited to two WAN connections
- Should dynamically detect and configure all available WAN interfaces

### GRE Tunnel Support
- Support for GRE tunnel connections (e.g., UniFi 5G modem)
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

### Scroll Position Restoration Between Routes
- Fix scroll position behavior when navigating between pages
- **Expected behavior:**
  - Navigating forward (clicking a link): Reset scroll to top
  - Navigating back (browser back button): Restore previous scroll position
- **Current behavior:** Scroll position is inconsistent/unpredictable
- **Implementation notes:**
  - Blazor Server doesn't have built-in scroll restoration
  - Browser's native scroll restoration doesn't work with SPAs (content renders after navigation)
  - Requires custom JS interop to save/restore scroll positions
  - Use browser history state or client-side cache keyed by route
  - Consider: `NavigationManager.LocationChanged` event + JS interop
- **Reference:** Similar to how browsers handle multi-page apps, but needs manual implementation for SPA

### Uniform Date/Time Formatting in UI
- Audit all date/time displays across the UI for consistency
- Standardize format (e.g., "Jan 4, 2026 3:45 PM" vs "2026-01-04 15:45:00")
- Consider user timezone preferences
- Affected areas: Speed test results, audit history, device last seen, logs

### ~~Replace Severity String Constants with Enums~~ (DONE)
- ~~Current: Severity comparisons use string literals like `"Critical"`, `"Recommended"`, `"Informational"`~~
- ~~Example: `i.Severity == "Critical"` scattered throughout codebase~~
- ~~Target: Use `AuditSeverity` enum consistently from backend to frontend~~
- ~~Benefits: Type safety, refactoring support, no string typos~~
- ~~Affected areas: AuditService, AlertsList, Audit.razor filtering/sorting~~
- DONE: Changed `AuditService.AuditIssue.Severity` to `AuditSeverity` enum, updated all comparisons

### ~~Unify Device Type System~~ (FIXED)
- ~~Multiple competing device type systems need consolidation~~
- FIXED: Consolidated to single `DeviceType` enum in `NetworkOptimizer.Core.Enums`
  - Includes all UniFi device types: Gateway, Switch, AccessPoint, CellularModem, BuildingBridge, CloudKey
  - Includes non-UniFi device types: Server, Desktop, Laptop
  - Extension methods: `ToDisplayName()`, `IsGateway()`, `IsUniFiNetworkDevice()`, `FromUniFiApiType()`
  - Stored as string in database via EF Core conversion for backwards compatibility
  - Deleted redundant `DeviceTypes` string constants and `UniFiDeviceTypes` helper class

## UniFi Device Classification (v2 API)

The UniFi v2 device API (`/proxy/network/v2/api/site/{site}/device`) returns multiple device arrays for improved device classification and VLAN security auditing.

### Device Arrays from v2 API

| Array | Description | VLAN Recommendation | Status |
|-------|-------------|---------------------|--------|
| `network_devices` | APs, Switches, Gateways | Management VLAN | Existing |
| `protect_devices` | Cameras, Doorbells, NVRs, Sensors | Security VLAN | **In Progress** |
| `access_devices` | Door locks, readers | Security VLAN | TODO |
| `connect_devices` | EV chargers, other Connect devices | IoT VLAN | TODO |
| `talk_devices` | Intercoms, phones | IoT/VoIP VLAN | TODO |
| `led_devices` | LED controllers, lighting | IoT VLAN | TODO |

### Phase 1: Protect Devices (Current)
- [x] Create `UniFiProtectDeviceResponse` model
- [x] Add `GetAllDevicesV2Async()` API method
- [x] Add `GetProtectCameraMacsAsync()` helper
- [x] Update `DeviceTypeDetectionService` to check Protect MACs first
- [x] Wire up in audit engine to fetch and use Protect MACs
- [x] Test camera detection with 100% confidence

### ~~Cloud vs Self-Hosted Camera Detection~~ (DONE)
- ~~Differentiate between cloud cameras and self-hosted/local cameras for VLAN recommendations~~
- ~~**Cloud Cameras** (should recommend IoT VLAN):~~
  - ~~Ring, Nest/Google, Wyze, Blink, Arlo (cloud-dependent)~~
  - ~~These require internet access and cloud services to function~~
  - ~~IoT VLAN with internet access is appropriate~~
- ~~**Self-Hosted Cameras** (should recommend Security/Camera VLAN):~~
  - ~~UniFi Protect / Ubiquiti cameras~~
  - ~~Eufy (local storage models)~~
  - ~~ONVIF/RTSP cameras recording to local NVR~~
  - ~~These can operate without internet, benefit from isolated security VLAN~~
- ~~Detection methods:~~
  - ~~MAC OUI lookup for manufacturer identification~~
  - ~~UniFi Protect API (already integrated) for Ubiquiti cameras~~
  - ~~Hostname/fingerprint patterns for cloud services~~
- ~~Implementation:~~
  - ~~Extend `DeviceTypeDetectionService` with cloud vs local camera classification~~
  - ~~Update `CameraVlanRule` to provide different recommendations based on camera type~~
  - ~~Consider new category: `ClientDeviceCategory.CloudCamera` vs existing camera category~~
- DONE: Added `ClientDeviceCategory.CloudCamera` enum, updated MacOuiDetector and DeviceTypeDetectionService for cloud vendor detection, CameraVlanRule skips cloud cameras (handled by IoT rules)

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
