# NetworkOptimizer.Reports

Professional PDF and Markdown report generation library for UniFi network audit reports.

## Features

- **Professional PDF Reports** using QuestPDF
  - Executive summary with security posture rating (EXCELLENT/GOOD/FAIR/NEEDS WORK)
  - Network topology overview with VLAN mapping
  - Critical issues section with red highlighting
  - Recommended improvements section
  - Per-device detailed port analysis tables
  - Color-coded status indicators (green=ok, yellow=warning, red=critical)
  - Optional company logo
  - Customizable branding for MSP white-labeling

- **Markdown Reports**
  - Same structure as PDF reports
  - Suitable for wikis, ticketing systems, version control
  - GitHub-flavored markdown tables
  - Emoji indicators for issue severity

## Installation

```bash
dotnet add package QuestPDF
```

## Usage

### Basic PDF Report

```csharp
using NetworkOptimizer.Reports;

// Create report data
var reportData = new ReportData
{
    ClientName = "Acme Corporation",
    GeneratedAt = DateTime.Now,
    SecurityScore = new SecurityScore
    {
        Rating = SecurityRating.Good,
        CriticalIssueCount = 0,
        WarningCount = 2
    },
    Networks = new List<NetworkInfo>
    {
        new() { Name = "Main", VlanId = 1, Subnet = "192.168.1.0/24" },
        new() { Name = "IoT", VlanId = 42, Subnet = "192.168.42.0/24" }
    },
    Switches = new List<SwitchDetail>
    {
        new()
        {
            Name = "Core Switch",
            Model = "USW-Pro-24-PoE",
            ModelName = "USW-Pro-24-PoE",
            IpAddress = "192.168.1.2",
            Ports = new List<PortDetail>
            {
                new()
                {
                    PortIndex = 1,
                    Name = "Uplink to Gateway",
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
                    PortSecurityMacs = new List<string> { "aa:bb:cc:dd:ee:ff" }
                }
            }
        }
    }
};

// Generate PDF with default Ozark Connect branding
var pdfGenerator = new PdfReportGenerator();
pdfGenerator.GenerateReport(reportData, "network_audit.pdf");
```

### Markdown Report

```csharp
// Generate Markdown
var mdGenerator = new MarkdownReportGenerator();
mdGenerator.GenerateReport(reportData, "network_audit.md");
```

### Custom Branding (MSP White-Label)

```csharp
// Create custom branding
var branding = new BrandingOptions
{
    CompanyName = "My MSP Company",
    LogoPath = "path/to/logo.png",
    Colors = new ColorScheme
    {
        Primary = "#1F4788",      // Your brand blue
        Secondary = "#5C7A99",    // Accent color
        Success = "#27AE60",
        Warning = "#F39C12",
        Critical = "#E74C3C"
    },
    ShowProductAttribution = true,
    ProductName = "NetworkOptimizer"
};

// Generate with custom branding
var pdfGenerator = new PdfReportGenerator(branding);
pdfGenerator.GenerateReport(reportData, "branded_report.pdf");
```

### Pre-defined Color Schemes

```csharp
// Ozark Connect branding (default)
var ozarkBranding = BrandingOptions.OzarkConnect();

// Generic/unbranded
var genericBranding = BrandingOptions.Generic();

// High contrast (accessibility)
var colorScheme = ColorScheme.HighContrast();
var accessibleBranding = new BrandingOptions
{
    Colors = colorScheme
};
```

## Report Structure

### 1. Executive Summary
- Overall security posture rating
- Hardening measures already in place
- Network topology notes

### 2. Network Reference
- VLAN mappings
- Subnet information
- Network purposes

### 3. Action Items
- **Critical Issues** (red) - Immediate attention required
  - IoT devices on wrong VLAN
  - Security misconfigurations
- **Recommended Improvements** (yellow) - Best practice enhancements
  - Missing MAC restrictions
  - Unused ports not disabled

### 4. Per-Device Port Analysis
- Port index and name
- Link status (Up 1 GbE, Down, etc.)
- Forward mode (native, all, disabled)
- Native VLAN assignment
- PoE power consumption
- Port security status (MAC restrictions)
- Port isolation status
- Overall status indicator

### 5. Port Security Coverage Summary
- Total ports per switch
- Disabled ports count
- MAC-restricted ports count
- Unprotected active ports count

## Color Coding

### Status Indicators
- ✓ Green: OK, configured correctly
- ⚠ Yellow: Warning, recommended improvement
- ■ Red: Critical issue, immediate action required

### Ozark Connect Brand Colors
- **Primary (Teal)**: `#2E6B7D` - Headers, primary accents
- **Secondary (Orange)**: `#E87D33` - Secondary accents
- **Tertiary (Blue)**: `#215999` - Additional accents
- **Success (Green)**: `#389E3C` - OK status
- **Warning (Yellow)**: `#D9A621` - Caution status
- **Critical (Red)**: `#CC3333` - Error status

## Security Ratings

The overall security posture is automatically calculated:

- **EXCELLENT** ✓ - Zero critical issues, zero warnings
- **GOOD** ✓ - Zero critical issues, some warnings
- **FAIR** ⚠ - 1-2 critical issues
- **NEEDS ATTENTION** ✗ - 3+ critical issues

```csharp
var rating = SecurityScore.CalculateRating(
    criticalCount: 0,
    warningCount: 2
); // Returns SecurityRating.Good
```

## Logo Requirements

For best results with company logos:

- **Format**: PNG with transparent background
- **Size**: 400x286 pixels (1.4:1 aspect ratio)
- **Display**: Renders at 1.5 inches wide in PDF

## Advanced Usage

### Generate In-Memory PDF

```csharp
var pdfGenerator = new PdfReportGenerator();
byte[] pdfBytes = pdfGenerator.GenerateReportBytes(reportData);

// Stream to browser, save to blob storage, etc.
```

### Generate Markdown String

```csharp
var mdGenerator = new MarkdownReportGenerator();
string markdown = mdGenerator.GenerateMarkdown(reportData);

// Post to API, save to database, etc.
```

### Building Report Data from UniFi API

```csharp
// Example: Convert UniFi API response to ReportData
var reportData = new ReportData
{
    ClientName = clientName,
    GeneratedAt = DateTime.Now
};

// Parse networks from gateway's network_table
foreach (var network in unifiNetworks)
{
    reportData.Networks.Add(new NetworkInfo
    {
        NetworkId = network.Id,
        Name = network.Name,
        VlanId = network.Vlan ?? 1,
        Subnet = network.IpSubnet,
        Purpose = network.Purpose
    });
}

// Parse switches and ports
foreach (var device in unifiDevices.Where(d => d.PortTable?.Any() == true))
{
    var switchDetail = new SwitchDetail
    {
        Name = device.Name,
        Mac = device.Mac,
        Model = device.Model,
        ModelName = GetFriendlyModelName(device.Model),
        IpAddress = device.Ip,
        IsGateway = device.Type == "udm" || device.Type == "ugw",
        MaxCustomMacAcls = device.SwitchCaps?.MaxCustomMacAcls ?? 0
    };

    foreach (var port in device.PortTable)
    {
        switchDetail.Ports.Add(new PortDetail
        {
            PortIndex = port.PortIdx,
            Name = port.Name ?? $"Port {port.PortIdx}",
            IsUp = port.Up,
            Speed = port.Speed,
            Forward = port.Forward,
            IsUplink = port.IsUplink,
            NativeNetwork = GetNetworkName(port.NativeNetworkConfId),
            NativeVlan = GetNetworkVlan(port.NativeNetworkConfId),
            PoePower = port.PoePower,
            PoeMode = port.PoeMode,
            PortSecurityEnabled = port.PortSecurityEnabled,
            PortSecurityMacs = port.PortSecurityMacAddress ?? new(),
            Isolation = port.Isolation
        });
    }

    reportData.Switches.Add(switchDetail);
}

// Analyze and populate issues
AnalyzeAndPopulateIssues(reportData);

// Calculate security score
reportData.SecurityScore.Rating = SecurityScore.CalculateRating(
    reportData.CriticalIssues.Count,
    reportData.RecommendedImprovements.Count
);
```

## Dependencies

- **.NET 8.0+**
- **QuestPDF 2024.12.3+** - PDF generation library

## License

This library is designed for use with NetworkOptimizer. QuestPDF uses a Community License for non-commercial use.

## Support

For issues and questions, please refer to the main NetworkOptimizer documentation.
