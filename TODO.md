# Network Optimizer - TODO / Future Enhancements

## LAN Speed Test

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

## Distribution

### ISO/OVA Image for MSP Deployment
- Create distributable ISO and/or OVA image for MSP users
- Pre-configured Linux appliance with Network Optimizer installed
- Easy deployment to customer sites without Docker expertise
- Consider: Ubuntu Server base, auto-updates, web-based initial setup

## General

### ~~Unify Device Type System~~ (FIXED)
- ~~Multiple competing device type systems need consolidation~~
- FIXED: Consolidated to single `DeviceType` enum in `NetworkOptimizer.Core.Enums`
  - Includes all UniFi device types: Gateway, Switch, AccessPoint, CellularModem, BuildingBridge, CloudKey
  - Includes non-UniFi device types: Server, Desktop, Laptop
  - Extension methods: `ToDisplayName()`, `IsGateway()`, `IsUniFiNetworkDevice()`, `FromUniFiApiType()`
  - Stored as string in database via EF Core conversion for backwards compatibility
  - Deleted redundant `DeviceTypes` string constants and `UniFiDeviceTypes` helper class
