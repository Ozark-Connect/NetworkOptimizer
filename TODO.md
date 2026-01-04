# Network Optimizer - TODO / Future Enhancements

## LAN Speed Test

### 🔥 Non-SSH Device Speed Testing (HIGH PRIORITY)
Current limitation: LAN speed tests require SSH access to target devices (UniFi equipment only).

**Goal:** Enable speed testing from any device (phones, tablets, laptops, IoT) without SSH.

**Approach Options:**

1. **Server-side iperf3 monitoring**
   - Run `iperf3 -s` on the Network Optimizer server
   - Monitor stdout for incoming test results
   - Build lightweight clients:
     - iOS app
     - Android app
     - Web client (simplest, works everywhere)
   - Server captures results and associates with device

2. **Branded OpenSpeedTest integration** (preferred)
   - Host a branded OpenSpeedTest instance
   - User runs browser-based speed test from any device
   - After test completes, import/correlate results into Network Optimizer
   - Benefits: No app install, works on any device with a browser
   - Challenge: Correlating browser client to network device identity

**Implementation considerations:**
- Device identification via source IP (sufficient - can correlate to UniFi client list)
- Result storage and historical tracking
- Integration with existing path analysis

**Status:** In Progress - implementing both approaches

### ~~Retransmit Analysis~~ (FIXED)
- ~~Flag high retransmit counts as a separate insight~~
- ~~Example: "High packet loss detected on return path" when retransmits are asymmetric~~
- ~~Observation: Front Yard AP test showed 3181 retransmits "to device" but 0 "from device"~~
- ~~This asymmetry on wireless mesh links makes sense (mesh uplink contention)~~
- FIXED: PathAnalysisResult.AnalyzeRetransmits() detects high/asymmetric retransmits and generates insights

### Path Analysis Enhancements
- Direction-aware bottleneck calculation (separate from/to max speeds)
- More gateway models in routing limits table as we gather data
- Threshold tuning based on real-world data collection

## Security Audit / PDF Report

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

### Replace Severity String Constants with Enums
- Current: Severity comparisons use string literals like `"Critical"`, `"Recommended"`, `"Informational"`
- Example: `i.Severity == "Critical"` scattered throughout codebase
- Target: Use `AuditSeverity` enum consistently from backend to frontend
- Benefits: Type safety, refactoring support, no string typos
- Affected areas: AuditService, AlertsList, Audit.razor filtering/sorting

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
