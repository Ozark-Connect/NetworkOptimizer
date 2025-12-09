using NetworkOptimizer.Reports;

namespace NetworkOptimizer.Reports.Examples;

/// <summary>
/// Sample code demonstrating report generation
/// This class shows how to build report data and generate PDF/Markdown reports
/// </summary>
public static class SampleReportGeneration
{
    /// <summary>
    /// Generate a complete sample report with realistic network data
    /// </summary>
    public static void GenerateCompleteSampleReport(string outputDirectory)
    {
        // Create sample report data
        var reportData = BuildSampleReportData();

        // Generate PDF with Ozark Connect branding
        var pdfGenerator = new PdfReportGenerator(BrandingOptions.OzarkConnect());
        var pdfPath = Path.Combine(outputDirectory, "sample_network_audit.pdf");
        pdfGenerator.GenerateReport(reportData, pdfPath);
        Console.WriteLine($"PDF report generated: {pdfPath}");

        // Generate Markdown
        var mdGenerator = new MarkdownReportGenerator(BrandingOptions.OzarkConnect());
        var mdPath = Path.Combine(outputDirectory, "sample_network_audit.md");
        mdGenerator.GenerateReport(reportData, mdPath);
        Console.WriteLine($"Markdown report generated: {mdPath}");
    }

    /// <summary>
    /// Generate report with custom branding (MSP white-label example)
    /// </summary>
    public static void GenerateWhiteLabelReport(string outputDirectory, string companyName, string? logoPath = null)
    {
        var reportData = BuildSampleReportData();
        reportData.ClientName = companyName;

        // Custom branding
        var branding = new BrandingOptions
        {
            CompanyName = "TechPro MSP",
            LogoPath = logoPath,
            Colors = new ColorScheme
            {
                Primary = "#1F4788",      // Custom blue
                Secondary = "#5C7A99",
                Success = "#27AE60",
                Warning = "#F39C12",
                Critical = "#E74C3C"
            },
            ShowProductAttribution = true,
            ProductName = "NetworkOptimizer Pro"
        };

        var pdfGenerator = new PdfReportGenerator(branding);
        var pdfPath = Path.Combine(outputDirectory, $"{companyName.Replace(" ", "_")}_audit.pdf");
        pdfGenerator.GenerateReport(reportData, pdfPath);
        Console.WriteLine($"White-label PDF generated: {pdfPath}");
    }

    /// <summary>
    /// Build realistic sample report data
    /// </summary>
    private static ReportData BuildSampleReportData()
    {
        var data = new ReportData
        {
            ClientName = "Acme Corporation",
            GeneratedAt = DateTime.Now
        };

        // Networks
        data.Networks.AddRange(new[]
        {
            new NetworkInfo
            {
                NetworkId = "net1",
                Name = "Main",
                VlanId = 1,
                Subnet = "192.168.1.0/24",
                Purpose = "corporate",
                Type = NetworkType.Corporate
            },
            new NetworkInfo
            {
                NetworkId = "net2",
                Name = "IoT Zero Trust",
                VlanId = 42,
                Subnet = "192.168.42.0/24",
                Purpose = "guest",
                Type = NetworkType.IoT
            },
            new NetworkInfo
            {
                NetworkId = "net3",
                Name = "Security",
                VlanId = 50,
                Subnet = "192.168.50.0/24",
                Purpose = "guest",
                Type = NetworkType.Security
            },
            new NetworkInfo
            {
                NetworkId = "net4",
                Name = "Guest",
                VlanId = 99,
                Subnet = "192.168.99.0/24",
                Purpose = "guest",
                Type = NetworkType.Guest
            }
        });

        // Gateway
        var gateway = new SwitchDetail
        {
            Name = "Main Gateway",
            Mac = "aa:bb:cc:dd:ee:01",
            Model = "UCG-Ultra",
            ModelName = "UCG-Ultra",
            DeviceType = "ucg",
            IpAddress = "192.168.1.1",
            IsGateway = true,
            MaxCustomMacAcls = 128
        };

        gateway.Ports.AddRange(new[]
        {
            new PortDetail
            {
                PortIndex = 1,
                Name = "WAN",
                IsUp = true,
                Speed = 1000,
                Forward = "all",
                IsUplink = false
            },
            new PortDetail
            {
                PortIndex = 2,
                Name = "Uplink to Core",
                IsUp = true,
                Speed = 1000,
                Forward = "all",
                IsUplink = true
            }
        });

        data.Switches.Add(gateway);

        // Enterprise Switch
        var coreSwitch = new SwitchDetail
        {
            Name = "Core Switch",
            Mac = "aa:bb:cc:dd:ee:02",
            Model = "USW-Enterprise-8-PoE",
            ModelName = "USW-Enterprise-8-PoE",
            DeviceType = "usw",
            IpAddress = "192.168.1.2",
            IsGateway = false,
            MaxCustomMacAcls = 256
        };

        coreSwitch.Ports.AddRange(new[]
        {
            new PortDetail
            {
                PortIndex = 1,
                Name = "Uplink to Gateway",
                IsUp = true,
                Speed = 1000,
                Forward = "all",
                IsUplink = true
            },
            new PortDetail
            {
                PortIndex = 2,
                Name = "Server",
                IsUp = true,
                Speed = 1000,
                Forward = "native",
                NativeNetwork = "Main",
                NativeVlan = 1,
                PoePower = 0,
                PortSecurityMacs = new List<string> { "11:22:33:44:55:66" }
            },
            new PortDetail
            {
                PortIndex = 3,
                Name = "Office AP",
                IsUp = true,
                Speed = 1000,
                Forward = "custom",
                NativeNetwork = "Main",
                NativeVlan = 1,
                PoePower = 12.5,
                PoeMode = "auto",
                PortSecurityMacs = new List<string> { "22:33:44:55:66:77" }
            },
            new PortDetail
            {
                PortIndex = 4,
                Name = "Front Desk PC",
                IsUp = true,
                Speed = 1000,
                Forward = "native",
                NativeNetwork = "Main",
                NativeVlan = 1,
                PoePower = 0,
                PortSecurityMacs = new List<string> { "33:44:55:66:77:88" }
            },
            new PortDetail
            {
                PortIndex = 5,
                Name = "Security Camera 1",
                IsUp = true,
                Speed = 100,
                Forward = "native",
                NativeNetwork = "Security",
                NativeVlan = 50,
                PoePower = 8.2,
                PoeMode = "auto",
                Isolation = true,
                PortSecurityMacs = new List<string> { "44:55:66:77:88:99" }
            },
            new PortDetail
            {
                PortIndex = 6,
                Name = "Ikea Smart Bulb",  // IoT device on wrong VLAN - CRITICAL ISSUE
                IsUp = true,
                Speed = 100,
                Forward = "native",
                NativeNetwork = "Main",  // Should be on IoT VLAN!
                NativeVlan = 1,
                PoePower = 0
            },
            new PortDetail
            {
                PortIndex = 7,
                Name = "Spare",
                IsUp = false,
                Speed = 0,
                Forward = "disabled"
            },
            new PortDetail
            {
                PortIndex = 8,
                Name = "Spare",
                IsUp = false,
                Speed = 0,
                Forward = "disabled"
            }
        });

        data.Switches.Add(coreSwitch);

        // Issues
        data.CriticalIssues.Add(new AuditIssue
        {
            Type = IssueType.IoTWrongVlan,
            Severity = IssueSeverity.Critical,
            SwitchName = "Core Switch",
            PortIndex = 6,
            PortName = "Ikea Smart Bulb",
            CurrentNetwork = "Main",
            CurrentVlan = 1,
            RecommendedAction = "Move to IoT Zero Trust (42)",
            Message = "IoT device on Main VLAN"
        });

        data.RecommendedImprovements.Add(new AuditIssue
        {
            Type = IssueType.NoMacRestriction,
            Severity = IssueSeverity.Warning,
            SwitchName = "Core Switch",
            PortIndex = 6,
            PortName = "Ikea Smart Bulb",
            CurrentNetwork = "Main",
            CurrentVlan = 1,
            RecommendedAction = "Add MAC restriction",
            Message = "No MAC restriction on access port"
        });

        // Hardening notes
        data.HardeningNotes.AddRange(new[]
        {
            "All unused ports are disabled (forward: disabled)",
            "MAC restrictions on most single-device access ports",
            "Cameras properly isolated on Security VLAN with port isolation enabled"
        });

        data.TopologyNotes.AddRange(new[]
        {
            "UCG-Ultra gateway with dual WAN capability",
            "USW-Enterprise-8-PoE provides PoE for APs and cameras",
            "VLAN segmentation: Corporate (1), IoT (42), Security (50), Guest (99)"
        });

        // Security Score
        data.SecurityScore = new SecurityScore
        {
            Rating = SecurityScore.CalculateRating(
                data.CriticalIssues.Count,
                data.RecommendedImprovements.Count
            ),
            TotalDevices = data.Switches.Count,
            TotalPorts = data.Switches.Sum(s => s.TotalPorts),
            DisabledPorts = data.Switches.Sum(s => s.DisabledPorts),
            MacRestrictedPorts = data.Switches.Sum(s => s.MacRestrictedPorts),
            UnprotectedActivePorts = data.Switches.Sum(s => s.UnprotectedActivePorts),
            CriticalIssueCount = data.CriticalIssues.Count,
            WarningCount = data.RecommendedImprovements.Count
        };

        return data;
    }

    /// <summary>
    /// Example: Minimal report for testing
    /// </summary>
    public static ReportData BuildMinimalReport()
    {
        return new ReportData
        {
            ClientName = "Test Client",
            GeneratedAt = DateTime.Now,
            Networks = new List<NetworkInfo>
            {
                new() { Name = "Main", VlanId = 1, Subnet = "192.168.1.0/24" }
            },
            Switches = new List<SwitchDetail>
            {
                new()
                {
                    Name = "Test Switch",
                    Model = "USW-Flex-Mini",
                    ModelName = "USW-Flex-Mini",
                    IpAddress = "192.168.1.10",
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
                        }
                    }
                }
            },
            SecurityScore = new SecurityScore
            {
                Rating = SecurityRating.Excellent
            }
        };
    }

    /// <summary>
    /// Example: Report with all issue types
    /// </summary>
    public static ReportData BuildReportWithAllIssueTypes()
    {
        var data = BuildMinimalReport();

        data.CriticalIssues.AddRange(new[]
        {
            new AuditIssue
            {
                Type = IssueType.IoTWrongVlan,
                Severity = IssueSeverity.Critical,
                SwitchName = "Test Switch",
                PortIndex = 2,
                PortName = "Smart Device",
                Message = "IoT device on corporate VLAN",
                RecommendedAction = "Move to IoT VLAN"
            }
        });

        data.RecommendedImprovements.AddRange(new[]
        {
            new AuditIssue
            {
                Type = IssueType.NoMacRestriction,
                Severity = IssueSeverity.Warning,
                SwitchName = "Test Switch",
                PortIndex = 3,
                PortName = "Workstation",
                Message = "No MAC restriction on access port",
                RecommendedAction = "Add MAC restriction"
            },
            new AuditIssue
            {
                Type = IssueType.UnusedPortNotDisabled,
                Severity = IssueSeverity.Warning,
                SwitchName = "Test Switch",
                PortIndex = 4,
                PortName = "Unused Port",
                Message = "Unused port not disabled",
                RecommendedAction = "Disable unused port"
            }
        });

        data.SecurityScore.Rating = SecurityScore.CalculateRating(
            data.CriticalIssues.Count,
            data.RecommendedImprovements.Count
        );

        return data;
    }
}
