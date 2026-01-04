# NetworkOptimizer.Audit

Comprehensive security audit engine for UniFi network configurations. Analyzes switch ports, VLANs, firewall rules, and network topology to identify security issues and provide actionable recommendations.

## Features

### Port Security Analysis
- **IoT Device Detection**: Identifies IoT devices (IKEA, Hue, Smart devices, etc.) on incorrect VLANs
- **Camera Placement**: Ensures security cameras are on dedicated security VLANs
- **MAC Restrictions**: Detects access ports without MAC address filtering
- **Unused Ports**: Flags unused ports that aren't disabled
- **Port Isolation**: Checks for missing isolation on sensitive devices

### Firewall Rule Analysis
- **Shadowed Rules**: Detects rules that will never be hit due to earlier rules
- **Permissive Rules**: Identifies any->any rules and overly broad rules
- **Orphaned Rules**: Finds rules referencing deleted networks or groups
- **Inter-VLAN Isolation**: Checks for missing isolation between network segments

### VLAN/Network Analysis
- **Network Classification**: Automatically classifies networks (Corporate, IoT, Security, Guest, Management)
- **DNS Leakage Detection**: Identifies shared DNS servers between isolated networks
- **Gateway Configuration**: Checks for routing leakage between isolated VLANs
- **Network Topology Mapping**: Builds complete network map from UniFi configuration

### Security Scoring
- **0-100 Security Score**: Quantifiable security posture assessment
- **Weighted Scoring**: Critical issues weighted more heavily than recommendations
- **Hardening Bonus**: Rewards for security measures already in place
- **Posture Assessment**: Excellent, Good, Fair, Needs Attention, Critical

## Architecture

```
NetworkOptimizer.Audit/
├── Models/                           # Data models
│   ├── AuditResult.cs               # Complete audit results
│   ├── AuditIssue.cs                # Individual security finding
│   ├── AuditSeverity.cs             # Severity levels
│   ├── NetworkInfo.cs               # Network/VLAN information
│   ├── PortInfo.cs                  # Switch port configuration
│   ├── SwitchInfo.cs                # Switch device information
│   ├── WirelessClientInfo.cs        # Wireless client with detection
│   ├── OfflineClientInfo.cs         # Offline client with last network
│   ├── DeviceAllowanceSettings.cs   # Per-device-type VLAN allowances
│   └── FirewallRule.cs              # Firewall rule representation
│
├── Analyzers/                        # Analysis engines
│   ├── VlanAnalyzer.cs              # Network/VLAN analysis
│   ├── PortSecurityAnalyzer.cs      # Port security analysis
│   ├── FirewallRuleAnalyzer.cs      # Firewall rule analysis
│   ├── FirewallRuleParser.cs        # Parse UniFi firewall JSON
│   ├── FirewallRuleOverlapDetector.cs # Detect shadowed rules
│   └── AuditScorer.cs               # Security score calculation
│
├── Services/                         # Detection services
│   ├── DeviceTypeDetectionService.cs # Multi-source device detection
│   ├── IeeeOuiDatabase.cs           # IEEE OUI MAC vendor lookup
│   └── Detectors/
│       ├── MacOuiDetector.cs        # Detect by MAC OUI
│       ├── FingerprintDetector.cs   # Detect by UniFi fingerprint DB
│       └── NamePatternDetector.cs   # Detect by port/device name
│
├── Dns/                              # DNS security analysis
│   ├── DnsSecurityAnalyzer.cs       # DoH/DoT configuration analysis
│   ├── ThirdPartyDnsDetector.cs     # Detect Pi-hole, AdGuard, etc.
│   ├── DnsStampDecoder.cs           # Decode DNS stamp URLs
│   └── DohProviderRegistry.cs       # Known DoH provider database
│
├── Rules/                            # Individual audit rules
│   ├── IAuditRule.cs                # Rule interface
│   ├── IotVlanRule.cs               # Wired IoT VLAN placement
│   ├── WirelessIotVlanRule.cs       # Wireless IoT VLAN placement
│   ├── CameraVlanRule.cs            # Wired camera VLAN placement
│   ├── WirelessCameraVlanRule.cs    # Wireless camera VLAN placement
│   ├── VlanPlacementChecker.cs      # Shared VLAN recommendation logic
│   ├── MacRestrictionRule.cs        # MAC address filtering
│   ├── UnusedPortRule.cs            # Unused port detection
│   └── PortIsolationRule.cs         # Port isolation checks
│
├── ConfigAuditEngine.cs              # Main orchestrator
├── DeviceNameHints.cs                # Device type name patterns
└── IssueTypes.cs                     # Issue type constants
```

## Usage

### Basic Usage

```csharp
using NetworkOptimizer.Audit;
using Microsoft.Extensions.Logging;

// Create logger factory
var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

// Create audit engine
var auditEngine = new ConfigAuditEngine(
    loggerFactory.CreateLogger<ConfigAuditEngine>(),
    loggerFactory);

// Run audit from UniFi device JSON
var auditResult = await auditEngine.RunAuditFromFileAsync(
    "path/to/unifi_devices.json",
    clientName: "Example Corp"
);

// Check results
Console.WriteLine($"Security Score: {auditResult.SecurityScore}/100");
Console.WriteLine($"Posture: {auditResult.Posture}");
Console.WriteLine($"Critical Issues: {auditResult.CriticalIssues.Count}");
Console.WriteLine($"Recommended: {auditResult.RecommendedIssues.Count}");

// Get recommendations
var recommendations = auditEngine.GetRecommendations(auditResult);
foreach (var recommendation in recommendations)
{
    Console.WriteLine($"- {recommendation}");
}

// Generate text report
var report = auditEngine.GenerateTextReport(auditResult);
Console.WriteLine(report);

// Save results
auditEngine.SaveResults(auditResult, "audit_results.json", format: "json");
auditEngine.SaveResults(auditResult, "audit_results.txt", format: "text");
```

### Full Audit with All Data Sources

```csharp
// For best detection accuracy, provide all available data:
var auditResult = await auditEngine.RunAuditAsync(
    deviceDataJson: deviceJson,           // /stat/device response
    clients: clientList,                   // Connected clients for type detection
    clientHistory: historyList,            // Historical clients for offline detection
    fingerprintDb: uniFiFingerprintDb,     // UniFi fingerprint database
    settingsData: settingsJson,            // Site settings (DoH config)
    firewallPoliciesData: policiesJson,    // Firewall policies (DNS rules)
    allowanceSettings: allowances,         // Per-device-type VLAN allowances
    protectCameraMacs: cameraSet,          // UniFi Protect camera MACs
    clientName: "My Site"
);
```

### Device Type Detection

The audit engine uses multiple detection sources in priority order:

1. **UniFi Protect cameras** - 100% confidence for known Protect devices
2. **UniFi fingerprint database** - Device category from controller fingerprints
3. **IEEE OUI database** - Vendor lookup by MAC prefix (IKEA, Philips, etc.)
4. **Name patterns** - Port/device names containing IoT keywords

## IoT Device Detection

The engine automatically detects IoT devices based on port names containing:
- `ikea` - IKEA smart home devices
- `hue` - Philips Hue lighting
- `smart` - Generic smart devices
- `iot` - Explicitly labeled IoT
- `alexa` - Amazon Alexa devices
- `echo` - Amazon Echo devices
- `nest` - Google Nest devices
- `ring` - Ring doorbells/cameras
- `sonos` - Sonos speakers
- `philips` - Philips smart devices

## Camera Detection

Security cameras are detected by port names containing:
- `cam` - Camera
- `camera` - Full word
- `ptz` - Pan-Tilt-Zoom cameras
- `nvr` - Network Video Recorder
- `protect` - UniFi Protect

## Network Classification

Networks are automatically classified based on name patterns:

- **IoT**: iot, smart, device, automation, zero trust
- **Security**: camera, security, nvr, surveillance, protect
- **Management**: management, mgmt, admin
- **Guest**: guest, visitor
- **Corporate**: Default for main/corporate networks

## Security Score Calculation

The security score is calculated as:

```
Base Score: 100
- Critical Issues: Up to -50 points (sum of ScoreImpact, capped)
- Recommended Issues: Up to -30 points (sum of ScoreImpact, capped)
- Investigate Issues: Up to -10 points (sum of ScoreImpact, capped)
+ Hardening Bonus: Up to +8 points
  - Port hardening percentage (up to +5)
  - Number of hardening measures (up to +3)

Final Score: 0-100
```

### Score Interpretation

- **90-100**: Excellent - Outstanding security configuration
- **75-89**: Good - Solid security posture with minimal issues
- **60-74**: Fair - Acceptable but improvements recommended
- **40-59**: Needs Attention - Several issues require remediation
- **0-39**: Critical - Immediate attention required

## Input Data Format

The engine expects JSON data from the UniFi Controller API endpoint `/api/s/default/stat/device`:

```json
{
  "data": [
    {
      "type": "udm",
      "name": "Gateway",
      "mac": "...",
      "ip": "192.168.1.1",
      "network_table": [
        {
          "_id": "...",
          "name": "Main",
          "vlan": 1,
          "purpose": "corporate",
          "ip_subnet": "192.168.1.0/24"
        }
      ],
      "port_table": [
        {
          "port_idx": 1,
          "name": "Uplink",
          "up": true,
          "speed": 1000,
          "forward": "all",
          "is_uplink": true
        }
      ],
      "firewall_rules": [...]
    }
  ]
}
```

## Output Formats

### JSON Export
Complete audit results in structured JSON format suitable for:
- API integration
- Database storage
- Further processing

### Text Report
Human-readable text report with:
- Executive summary
- Network topology
- Statistics
- Critical issues
- Recommendations
- Switch details

## Extensibility

The engine is designed for extensibility:

1. **Custom Rules**: Implement `IAuditRule` for custom checks
2. **Custom Analyzers**: Add new analyzer classes
3. **Custom Scoring**: Extend `AuditScorer` for custom metrics
4. **Custom Reports**: Process `AuditResult` for custom output formats

## Dependencies

- .NET 10.0
- Microsoft.Extensions.Logging.Abstractions

## Thread Safety

The engine is thread-safe for read operations. Create separate instances for concurrent audits.

## Performance

Typical performance for a mid-sized network:
- 5 switches, 100 ports: < 500ms
- 10 networks, 50 firewall rules: < 200ms
- Complete audit: < 1 second

## Python Reference

This C# implementation is ported from the Python audit script at:
`OzarkConnect\UniFiNetworkReport\generate_port_audit.py`

Key enhancements over the Python version:
- Strongly-typed models
- Extensible rule engine
- Comprehensive firewall analysis
- Security scoring system
- Multiple output formats
- Production-ready error handling

## License

Business Source License 1.1. See [LICENSE](../../LICENSE) in the repository root.

© 2026 Ozark Connect
