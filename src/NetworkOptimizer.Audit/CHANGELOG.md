# Changelog

All notable changes to NetworkOptimizer.Audit will be documented in this file.

## [0.5.0] - 2026-01-03

### Added
- **DNS Security Analysis** (`DnsSecurityAnalyzer`)
  - DoH (DNS-over-HTTPS) configuration detection
  - DoT (DNS-over-TLS) configuration detection
  - DNS stamp URL decoding
  - Third-party DNS detection (Pi-hole, AdGuard, etc.)
  - DNS leak prevention rule analysis
  - WAN DNS server validation
- **Device Type Detection Service** (`DeviceTypeDetectionService`)
  - Multi-source detection with confidence scoring
  - Priority-based detection: Protect cameras > Fingerprint DB > OUI > Name patterns
  - IEEE OUI database integration for vendor detection
  - UniFi fingerprint database support
- **Wireless Client Analysis**
  - `WirelessClientInfo` model for wireless device tracking
  - VLAN placement analysis for wireless IoT/cameras
  - Access point association tracking
- **Offline Client Detection**
  - `OfflineClientInfo` model for historical client analysis
  - VLAN placement issues for recently-offline devices
  - Two-week recency threshold for severity scoring
- **Device Allowance Settings**
  - Per-device-type VLAN allowance configuration
  - Low-risk device handling (e.g., Philips Hue on main VLAN)
  - Vendor-specific allowances
- **UniFi Protect Integration**
  - Highest-priority detection for Protect camera MACs
  - 100% confidence for known Protect devices

### Changed
- Renamed `SecurityAuditEngine` to `PortSecurityAnalyzer`
- Upgraded to .NET 10.0
- Updated Microsoft.Extensions.Logging.Abstractions to 10.0.1
- Refactored firewall analysis into `FirewallRuleParser` and `FirewallRuleAnalyzer`
- Added `FirewallRuleOverlapDetector` for shadowed rule detection
- Improved network classification with purpose-based detection

### Fixed
- Firewall rule shadowing detection now handles complex subnet overlap scenarios
- Port isolation checks account for uplink ports correctly

---

## [0.1.0] - 2024-12-08

### Added
- Initial release of NetworkOptimizer.Audit
- Core audit engine (`ConfigAuditEngine`) for comprehensive UniFi network analysis
- Port security analysis with `PortSecurityAnalyzer`
  - IoT device VLAN placement detection
  - Camera VLAN placement detection
  - MAC restriction analysis
  - Unused port detection
  - Port isolation checks
- Network/VLAN analysis with `VlanAnalyzer`
  - Automatic network classification (Corporate, IoT, Security, Guest, Management)
  - DNS leakage detection
  - Gateway configuration analysis
  - Network topology mapping
- Firewall rule analysis with `FirewallRuleAnalyzer`
  - Shadowed rule detection
  - Overly permissive rule detection (any->any)
  - Orphaned rule detection
  - Inter-VLAN isolation checks
- Security scoring system with `AuditScorer`
  - 0-100 security posture score
  - Weighted severity-based deductions
  - Hardening bonus calculation
  - Security posture assessment (Excellent, Good, Fair, Needs Attention, Critical)
- Extensible rule engine
  - `IAuditRule` interface for custom rules
  - `AuditRuleBase` base class with helper methods
  - Five built-in rules:
    - `IotVlanRule` - IoT device placement
    - `CameraVlanRule` - Camera placement
    - `MacRestrictionRule` - MAC filtering
    - `UnusedPortRule` - Unused port detection
    - `PortIsolationRule` - Port isolation
- Comprehensive data models
  - `AuditResult` - Complete audit results
  - `AuditIssue` - Individual findings
  - `NetworkInfo` - Network/VLAN configuration
  - `PortInfo` - Switch port details
  - `SwitchInfo` - Switch device information
  - `FirewallRule` - Firewall rule representation
- Multiple output formats
  - JSON export for API integration
  - Text report for human consumption
  - Programmatic access to all data
- IoT device detection patterns
  - IKEA, Hue, Smart, Alexa, Echo, Nest, Ring, Sonos, Philips
- Camera detection patterns
  - Cam, Camera, PTZ, NVR, Protect
- Network classification patterns
  - IoT, Security, Management, Guest, Corporate
- Comprehensive README with usage examples
- Example usage code in `Examples/BasicUsageExample.cs`

### Ported from Python
- Based on `generate_port_audit.py` from OzarkConnect UniFi Network Report
- Enhanced with:
  - Strongly-typed C# models
  - Extensible rule engine
  - Firewall analysis (not in original Python)
  - Security scoring system (not in original Python)
  - Multiple analyzers for separation of concerns
  - Production-ready error handling

### Technical Details
- Target Framework: .NET 10.0
- Dependencies: Microsoft.Extensions.Logging.Abstractions 10.0.1
- Thread-safe for read operations
- Performance: < 1 second for typical mid-sized networks
- Comprehensive logging support via Microsoft.Extensions.Logging

### Documentation
- Complete README.md with:
  - Feature overview
  - Architecture diagram
  - Usage examples
  - IoT/Camera detection patterns
  - Network classification rules
  - Security scoring methodology
  - Input/output format specifications
  - Extensibility guide
- Inline XML documentation on all public APIs
- Usage examples for common scenarios
- CHANGELOG.md (this file)

## Future Enhancements (Planned)

### [1.1.0] - Planned
- [ ] VLAN spanning tree analysis
- [ ] Storm control configuration checks
- [ ] DHCP snooping analysis
- [ ] ARP inspection validation
- [ ] PoE budget analysis
- [ ] Link aggregation (LAG) configuration checks

### [1.2.0] - Planned
- [ ] Historical audit comparison
- [ ] Trend analysis over time
- [ ] Compliance framework mapping (CIS, NIST)
- [ ] Custom rule templates
- [ ] Rule configuration via JSON/YAML

### [2.0.0] - Planned
- [ ] Multi-site audit support
- [ ] Audit scheduling and automation
- [ ] Webhook notifications for critical issues
- [ ] Dashboard/UI for audit results
- [ ] Remediation automation (apply fixes)
- [ ] PDF report generation

## Breaking Changes

None (initial release)

## Known Issues

- Firewall rule shadowing detection uses simplified matching
  - May not detect all complex shadowing scenarios
  - More sophisticated subnet matching planned for 1.1.0
- Network classification is pattern-based
  - May misclassify networks with non-standard names
  - Manual override mechanism planned for 1.1.0
- No support for multi-controller environments
  - Single-site audit only in 1.0.0
  - Multi-site planned for 2.0.0

## Migration Guide

N/A (initial release)

## Contributors

- Initial development based on Python audit script from OzarkConnect project
- Enhanced and ported to C# for NetworkOptimizer.Audit

---

**Note**: This project follows [Semantic Versioning](https://semver.org/).
