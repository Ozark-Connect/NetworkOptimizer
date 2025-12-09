# Quick Start Guide

Get up and running with NetworkOptimizer.Audit in 5 minutes.

## Installation

### Add to Existing Project

```bash
# If you have a solution file
dotnet sln add src/NetworkOptimizer.Audit/NetworkOptimizer.Audit.csproj

# Reference from another project
dotnet add reference ../NetworkOptimizer.Audit/NetworkOptimizer.Audit.csproj
```

### Build

```bash
cd src/NetworkOptimizer.Audit
dotnet build
```

## Minimal Example

```csharp
using NetworkOptimizer.Audit;
using Microsoft.Extensions.Logging;

// 1. Create a logger
var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Warning); // Quiet mode
});

var logger = loggerFactory.CreateLogger<ConfigAuditEngine>();

// 2. Create the audit engine
var engine = new ConfigAuditEngine(logger);

// 3. Run the audit
var result = engine.RunAuditFromFile(
    "path/to/unifi_devices.json",
    clientName: "My Network"
);

// 4. View the results
Console.WriteLine($"Security Score: {result.SecurityScore}/100");
Console.WriteLine($"Security Posture: {result.Posture}");
Console.WriteLine($"Critical Issues: {result.CriticalIssues.Count}");

// 5. Save the report
engine.SaveResults(result, "audit_report.txt", "text");
```

## Getting UniFi Device Data

### Option 1: From UniFi Controller API

```bash
# Using curl (replace with your controller details)
curl -k -X GET \
  https://unifi.local:8443/api/s/default/stat/device \
  -H 'Content-Type: application/json' \
  -H 'Cookie: unifises=...' \
  > devices.json
```

### Option 2: Using PowerShell

```powershell
# Login to UniFi Controller
$body = @{
    username = "admin"
    password = "your-password"
} | ConvertTo-Json

$session = Invoke-RestMethod -Uri "https://unifi.local:8443/api/login" `
    -Method Post -Body $body -SessionVariable websession `
    -SkipCertificateCheck

# Get device data
$devices = Invoke-RestMethod -Uri "https://unifi.local:8443/api/s/default/stat/device" `
    -WebSession $websession -SkipCertificateCheck

$devices | ConvertTo-Json -Depth 10 | Out-File "devices.json"
```

### Option 3: Export from UniFi Controller UI

1. Log into UniFi Controller
2. Go to Settings > System > Maintenance
3. Download site export (contains device data)
4. Extract the JSON files

## Understanding the Results

### Security Score Interpretation

| Score   | Posture          | Meaning                                      |
|---------|------------------|----------------------------------------------|
| 90-100  | Excellent        | Outstanding security - maintain current state|
| 75-89   | Good             | Solid security with minor improvements needed|
| 60-74   | Fair             | Acceptable but several improvements needed   |
| 40-59   | Needs Attention  | Multiple issues require remediation          |
| 0-39    | Critical         | Immediate action required                    |

### Issue Severity Levels

- **Critical**: Immediate security risk, fix ASAP
  - IoT devices on wrong VLAN
  - Cameras not isolated
  - Any->any firewall rules

- **Recommended**: Best practice improvements
  - Missing MAC restrictions
  - Unused ports not disabled
  - Missing port isolation

- **Investigate**: Requires review
  - DNS leakage potential
  - Shadowed firewall rules
  - Orphaned configurations

## Common Use Cases

### 1. Quick Health Check

```csharp
var result = engine.RunAuditFromFile("devices.json", "MyNetwork");

if (result.CriticalIssues.Any())
{
    Console.WriteLine("⚠ CRITICAL ISSUES FOUND!");
    foreach (var issue in result.CriticalIssues)
    {
        Console.WriteLine($"  - {issue.Message}");
    }
}
else
{
    Console.WriteLine("✓ No critical issues found");
}
```

### 2. Executive Report

```csharp
var result = engine.RunAuditFromFile("devices.json", "MyNetwork");
var summary = engine.GenerateExecutiveSummary(result);

// Email to management
SendEmail(
    to: "manager@company.com",
    subject: $"Network Security Audit - Score: {result.SecurityScore}/100",
    body: summary
);
```

### 3. Generate Full Report

```csharp
var result = engine.RunAuditFromFile("devices.json", "MyNetwork");

// Text report for review
var report = engine.GenerateTextReport(result);
File.WriteAllText("full_audit_report.txt", report);

// JSON for database/API
engine.SaveResults(result, "audit_data.json", "json");
```

### 4. Focus on Specific Issues

```csharp
var result = engine.RunAuditFromFile("devices.json", "MyNetwork");

// Find all IoT devices on wrong VLANs
var iotIssues = result.Issues
    .Where(i => i.Type.Contains("IOT"))
    .ToList();

Console.WriteLine($"Found {iotIssues.Count} IoT placement issues:");
foreach (var issue in iotIssues)
{
    Console.WriteLine($"  {issue.DeviceName} Port {issue.Port}: {issue.PortName}");
    Console.WriteLine($"    Current: {issue.CurrentNetwork}");
    Console.WriteLine($"    Recommended: {issue.RecommendedNetwork}");
}
```

### 5. Automated Daily Audits

```csharp
// Run daily audit and alert on score drops
var previousScore = LoadPreviousScore();
var result = engine.RunAuditFromFile("devices.json", "MyNetwork");

if (result.SecurityScore < previousScore - 10)
{
    AlertOps($"Security score dropped from {previousScore} to {result.SecurityScore}");
}

SaveScore(result.SecurityScore);
```

## Sample Output

### Console Output
```
Security Score: 85/100
Security Posture: Good
Critical Issues: 0
Recommended Improvements: 3

=== RECOMMENDATIONS ===
1. Implement MAC restrictions on 5 access ports
2. Disable 3 unused ports to reduce attack surface
3. Enable port isolation on 2 security-sensitive devices
```

### Text Report (Excerpt)
```
================================================================================
        UniFi Network Security Audit Report
        Client: Example Corp
        Generated: 2024-12-08 15:30:00 UTC
================================================================================

EXECUTIVE SUMMARY
--------------------------------------------------------------------------------
Security Posture: Good (Score: 85/100)

Excellent network security configuration with 3 recommended improvements.
80% of ports have security hardening measures applied.

HARDENING MEASURES IN PLACE
--------------------------------------------------------------------------------
  ✓ 12 unused ports disabled (40% of total ports)
  ✓ MAC restrictions configured on 15 access ports
  ✓ 4 cameras properly isolated on Security VLAN
```

## Troubleshooting

### Issue: "File not found"
```csharp
// Use absolute path
var path = Path.GetFullPath("devices.json");
var result = engine.RunAuditFromFile(path, "MyNetwork");
```

### Issue: "No networks found"
```csharp
// Check if JSON has correct structure
var json = File.ReadAllText("devices.json");
var doc = JsonDocument.Parse(json);

// Should have "data" array with devices
if (doc.RootElement.TryGetProperty("data", out var data))
{
    Console.WriteLine($"Found {data.GetArrayLength()} devices");
}
```

### Issue: Low score but no issues shown
```csharp
// Check all severity levels
Console.WriteLine($"Critical: {result.CriticalIssues.Count}");
Console.WriteLine($"Recommended: {result.RecommendedIssues.Count}");
Console.WriteLine($"Investigate: {result.InvestigateIssues.Count}");

// View all issues
foreach (var issue in result.Issues)
{
    Console.WriteLine($"[{issue.Severity}] {issue.Message}");
}
```

## Next Steps

1. **Review the README.md** for complete documentation
2. **Check Examples/BasicUsageExample.cs** for more usage patterns
3. **Customize rules** by implementing IAuditRule interface
4. **Integrate into CI/CD** for automated network audits
5. **Set up alerts** for critical security issues

## Getting Help

- Review the full [README.md](README.md)
- Check [CHANGELOG.md](CHANGELOG.md) for version history
- See [PROJECT_SUMMARY.md](PROJECT_SUMMARY.md) for architecture details
- Look at [Examples/BasicUsageExample.cs](Examples/BasicUsageExample.cs) for code samples

## Performance Tips

1. **Use appropriate log level**
   - Warning/Error for production
   - Information for development
   - Debug for troubleshooting

2. **Reuse engine instance**
   ```csharp
   var engine = new ConfigAuditEngine(logger);

   // Good - reuse for multiple audits
   var result1 = engine.RunAuditFromFile("site1.json", "Site1");
   var result2 = engine.RunAuditFromFile("site2.json", "Site2");
   ```

3. **Process large files efficiently**
   ```csharp
   // Stream large JSON files if needed
   using var fileStream = File.OpenRead("large_devices.json");
   using var doc = JsonDocument.Parse(fileStream);
   var result = engine.RunAudit(doc.RootElement.GetRawText(), "LargeSite");
   ```

## License

Copyright (c) 2024. All rights reserved.
