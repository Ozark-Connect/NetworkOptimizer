# Network Optimizer - TODO / Future Enhancements

## LAN Speed Test

### Retransmit Analysis
- Flag high retransmit counts as a separate insight
- Example: "High packet loss detected on return path" when retransmits are asymmetric
- Observation: Front Yard AP test showed 3181 retransmits "to device" but 0 "from device"
- This asymmetry on wireless mesh links makes sense (mesh uplink contention)

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

### Wireless Client Band Info
- Show WiFi band (2.4GHz/5GHz/6GHz) in the Port column for wireless issues
- Example: "on [AP] Tiny Home (5GHz)" instead of just "on [AP] Tiny Home"
- Data source: UniFi client response has radio/channel info

## SQM (Smart Queue Management)

### Multi-WAN Support
- Support for 3rd, 4th, and N number of WAN connections
- Currently limited to two WAN connections
- Should dynamically detect and configure all available WAN interfaces

### GRE Tunnel Support
- Support for GRE tunnel connections (e.g., UniFi 5G modem)
- Currently specifically excluded from SQM configuration
- These tunnels should be treated as valid WAN interfaces for SQM purposes

## General

- (Add future enhancements here)
