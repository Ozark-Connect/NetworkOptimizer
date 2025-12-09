# Quick Start Guide - NetworkOptimizer.Reports

Get up and running with professional network audit reports in 5 minutes.

## Installation

```bash
# Create new project or add to existing
dotnet new console -n MyAuditTool
cd MyAuditTool

# Add reference to NetworkOptimizer.Reports
dotnet add reference ../NetworkOptimizer.Reports/NetworkOptimizer.Reports.csproj

# Or add QuestPDF directly if using as standalone
dotnet add package QuestPDF
```

## 5-Minute Example

```csharp
using NetworkOptimizer.Reports;

// 1. Create report data
var report = new ReportData
{
    ClientName = "Acme Corporation",

    // 2. Add networks
    Networks = new List<NetworkInfo>
    {
        new() { Name = "Main", VlanId = 1, Subnet = "192.168.1.0/24" },
        new() { Name = "IoT", VlanId = 42, Subnet = "192.168.42.0/24" }
    },

    // 3. Add a switch with ports
    Switches = new List<SwitchDetail>
    {
        new()
        {
            Name = "Core Switch",
            Model = "USW-Pro-24-PoE",
            ModelName = "USW-Pro-24-PoE",
            IpAddress = "192.168.1.10",
            MaxCustomMacAcls = 256,

            Ports = new List<PortDetail>
            {
                new()
                {
                    PortIndex = 1,
                    Name = "Uplink",
                    IsUp = true,
                    Speed = 1000,
                    Forward = "all",
                    IsUplink = true
                },
                new()
                {
                    PortIndex = 2,
                    Name = "Server",
                    IsUp = true,
                    Speed = 1000,
                    Forward = "native",
                    NativeNetwork = "Main",
                    NativeVlan = 1,
                    PortSecurityMacs = new() { "aa:bb:cc:dd:ee:ff" }
                }
            }
        }
    },

    // 4. Add any critical issues
    CriticalIssues = new List<AuditIssue>
    {
        new()
        {
            Type = IssueType.IoTWrongVlan,
            Severity = IssueSeverity.Critical,
            SwitchName = "Core Switch",
            PortIndex = 5,
            PortName = "Smart Bulb",
            Message = "IoT device on corporate VLAN",
            RecommendedAction = "Move to IoT (42)"
        }
    }
};

// 5. Calculate security score
report.SecurityScore.Rating = SecurityScore.CalculateRating(
    report.CriticalIssues.Count,
    report.RecommendedImprovements.Count
);

// 6. Generate PDF
var pdfGen = new PdfReportGenerator();
pdfGen.GenerateReport(report, "network_audit.pdf");

// 7. Generate Markdown
var mdGen = new MarkdownReportGenerator();
mdGen.GenerateReport(report, "network_audit.md");

Console.WriteLine("Reports generated successfully!");
```

## Run the Examples

```bash
cd NetworkOptimizer.Reports/Examples
dotnet run

# Output:
# - sample_network_audit.pdf
# - sample_network_audit.md
# - Acme_Corporation_audit.pdf
# - minimal_report.pdf
# - all_issues_report.md
```

## Common Scenarios

### Scenario 1: Basic Report (No Issues)

```csharp
var report = new ReportData
{
    ClientName = "Happy Client",
    SecurityScore = new() { Rating = SecurityRating.Excellent }
};

new PdfReportGenerator().GenerateReport(report, "perfect_network.pdf");
// Result: "Overall Security Posture: EXCELLENT ✓"
```

### Scenario 2: Custom Branding (MSP)

```csharp
var branding = new BrandingOptions
{
    CompanyName = "TechPro MSP",
    LogoPath = "my_logo.png",
    Colors = new ColorScheme
    {
        Primary = "#1F4788",
        Success = "#27AE60",
        Critical = "#E74C3C"
    }
};

var generator = new PdfReportGenerator(branding);
generator.GenerateReport(report, "branded_report.pdf");
```

### Scenario 3: In-Memory PDF (Web API)

```csharp
// ASP.NET Core controller
[HttpGet("report/{clientId}")]
public IActionResult GetReport(string clientId)
{
    var report = _service.GetReportData(clientId);
    var generator = new PdfReportGenerator();
    var bytes = generator.GenerateReportBytes(report);

    return File(bytes, "application/pdf", $"{clientId}_audit.pdf");
}
```

### Scenario 4: Markdown for GitHub

```csharp
var report = BuildReportData();
var generator = new MarkdownReportGenerator();
var markdown = generator.GenerateMarkdown(report);

// Commit to git
File.WriteAllText("docs/network-audit.md", markdown);
// git add docs/network-audit.md
// git commit -m "Add network audit report"
```

## Data Mapping from UniFi API

### Step 1: Parse UniFi Response
```csharp
// GET /api/s/default/stat/device
var unifiDevices = await unifiClient.GetDevicesAsync();
```

### Step 2: Map to ReportData
```csharp
var report = new ReportData { ClientName = siteName };

// Map networks
var gateway = unifiDevices.First(d => d.Type == "udm");
foreach (var net in gateway.NetworkTable)
{
    report.Networks.Add(new NetworkInfo
    {
        NetworkId = net.Id,
        Name = net.Name,
        VlanId = net.Vlan ?? 1,
        Subnet = net.IpSubnet
    });
}

// Map switches
foreach (var device in unifiDevices.Where(d => d.PortTable?.Any() == true))
{
    var switchDetail = new SwitchDetail
    {
        Name = device.Name,
        Model = device.Model,
        IpAddress = device.Ip,
        MaxCustomMacAcls = device.SwitchCaps?.MaxCustomMacAcls ?? 0
    };

    foreach (var port in device.PortTable)
    {
        switchDetail.Ports.Add(new PortDetail
        {
            PortIndex = port.PortIdx,
            Name = port.Name,
            IsUp = port.Up,
            Speed = port.Speed,
            Forward = port.Forward,
            NativeVlan = GetVlanId(port.NativeNetworkConfId),
            PortSecurityMacs = port.PortSecurityMacAddress ?? new()
        });
    }

    report.Switches.Add(switchDetail);
}
```

### Step 3: Analyze Issues
```csharp
foreach (var sw in report.Switches)
{
    foreach (var port in sw.Ports.Where(p => p.IsUp))
    {
        // Check for IoT on wrong VLAN
        if (IsIoTDevice(port.Name) && !IsIoTVlan(port.NativeVlan))
        {
            report.CriticalIssues.Add(new AuditIssue
            {
                Type = IssueType.IoTWrongVlan,
                Severity = IssueSeverity.Critical,
                SwitchName = sw.Name,
                PortIndex = port.PortIndex,
                PortName = port.Name,
                Message = "IoT device on corporate VLAN",
                RecommendedAction = "Move to IoT VLAN"
            });
        }

        // Check for missing MAC restrictions
        if (port.Forward == "native" &&
            port.PortSecurityMacs.Count == 0 &&
            sw.MaxCustomMacAcls > 0)
        {
            report.RecommendedImprovements.Add(new AuditIssue
            {
                Type = IssueType.NoMacRestriction,
                Severity = IssueSeverity.Warning,
                SwitchName = sw.Name,
                PortIndex = port.PortIndex,
                PortName = port.Name,
                Message = "No MAC restriction on access port"
            });
        }
    }
}
```

### Step 4: Generate Report
```csharp
report.SecurityScore.Rating = SecurityScore.CalculateRating(
    report.CriticalIssues.Count,
    report.RecommendedImprovements.Count
);

new PdfReportGenerator().GenerateReport(report, "audit.pdf");
```

## Color Scheme Reference

### Ozark Connect (Default)
```csharp
Primary:    #2E6B7D  (Teal)
Secondary:  #E87D33  (Orange)
Success:    #389E3C  (Green)
Warning:    #D9A621  (Yellow)
Critical:   #CC3333  (Red)
```

### Generic Professional
```csharp
Primary:    #1F4788  (Blue)
Success:    #27AE60  (Green)
Warning:    #F39C12  (Orange)
Critical:   #E74C3C  (Red)
```

### High Contrast (Accessibility)
```csharp
Primary:    #000080  (Navy)
Success:    #006400  (Dark Green)
Warning:    #FF8C00  (Dark Orange)
Critical:   #8B0000  (Dark Red)
```

## Troubleshooting

### Issue: QuestPDF License Error
```
Solution: Add license setting before generating reports
QuestPDF.Settings.License = LicenseType.Community;
```

### Issue: Logo Not Showing
```
Solution: Verify logo path is absolute and file exists
var logoPath = Path.GetFullPath("logo.png");
branding.LogoPath = File.Exists(logoPath) ? logoPath : null;
```

### Issue: Special Characters in PDF
```
Solution: Ensure proper UTF-8 encoding
File.WriteAllText(path, content, Encoding.UTF8);
```

### Issue: Large Reports Slow
```
Solution: Generate in background task
await Task.Run(() => generator.GenerateReport(data, path));
```

## Next Steps

1. **Read README.md** - Comprehensive documentation
2. **Review Examples/** - Sample code and patterns
3. **Check PROJECT-SUMMARY.md** - Architecture details
4. **Integrate with UniFi API** - Build real reports
5. **Customize branding** - Add your logo and colors

## Support

- Review inline XML documentation (IntelliSense)
- Check Examples/SampleReportGeneration.cs for patterns
- See ReportData.cs for all available properties
- Refer to Python reference: OzarkConnect/UniFiNetworkReport/generate_port_audit.py

## License

QuestPDF Community License applies (free for non-commercial use).
For commercial MSP deployments, review QuestPDF Professional License.

---

**Ready to generate professional network audit reports!**
