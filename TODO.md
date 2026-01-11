# Network Optimizer - TODO / Future Enhancements

## LAN Speed Test

### Path Analysis Enhancements
- Direction-aware bottleneck calculation: TX and RX rates can differ significantly on Wi-Fi (e.g., client may have 800 Mbps TX but 400 Mbps RX); path analysis should use direction-appropriate rate for separate from/to max speeds
- More gateway models in routing limits table as we gather data
- Threshold tuning based on real-world data collection

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

### AP Pinning Report
- Report all devices that are pinned to specific APs
- For each pinned device, show:
  - Device name/type
  - Pinned AP name
  - How long it's been pinned
- Flag devices that probably shouldn't be pinned:
  - **Obvious cases:** Phones, tablets, wearables, laptops (mobile devices that roam)
  - **Borderline:** IoT devices (user prerogative, but often unnecessary)
  - **Acceptable:** Fixed cameras, sensors, stationary equipment
- Default stance: Pinning is the user's prerogative, but it's often not recommended because:
  - Device stays offline if its pinned AP goes down (no failover)
  - Prevents roaming to better AP when signal degrades
  - Can cause connectivity issues during AP firmware updates
- Severity: Informational for acceptable pins, Recommended for mobile device pins

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
