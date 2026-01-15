using FluentAssertions;
using NetworkOptimizer.Reports;
using Xunit;

namespace NetworkOptimizer.Reports.Tests;

public class ReportDataTests
{
    [Fact]
    public void ReportData_DefaultValues_AreCorrect()
    {
        // Act
        var data = new ReportData();

        // Assert
        data.ClientName.Should().Be("Client");
        data.GeneratedAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(5));
        data.SecurityScore.Should().NotBeNull();
        data.Networks.Should().BeEmpty();
        data.Devices.Should().BeEmpty();
        data.Switches.Should().BeEmpty();
        data.AccessPoints.Should().BeEmpty();
        data.OfflineClients.Should().BeEmpty();
        data.CriticalIssues.Should().BeEmpty();
        data.RecommendedImprovements.Should().BeEmpty();
        data.HardeningNotes.Should().BeEmpty();
        data.TopologyNotes.Should().BeEmpty();
        data.DnsSecurity.Should().BeNull();
    }
}

public class SecurityScoreTests
{
    #region CalculateRating Tests

    [Theory]
    [InlineData(0, 0, SecurityRating.Excellent)]
    [InlineData(0, 1, SecurityRating.Good)]
    [InlineData(0, 5, SecurityRating.Good)]
    [InlineData(0, 10, SecurityRating.Good)]
    [InlineData(1, 0, SecurityRating.Fair)]
    [InlineData(2, 0, SecurityRating.Fair)]
    [InlineData(2, 5, SecurityRating.Fair)]
    [InlineData(3, 0, SecurityRating.NeedsWork)]
    [InlineData(5, 0, SecurityRating.NeedsWork)]
    [InlineData(10, 10, SecurityRating.NeedsWork)]
    public void CalculateRating_VariousCounts_ReturnsCorrectRating(
        int criticalCount, int warningCount, SecurityRating expected)
    {
        // Act
        var rating = SecurityScore.CalculateRating(criticalCount, warningCount);

        // Assert
        rating.Should().Be(expected);
    }

    #endregion

    [Fact]
    public void SecurityScore_DefaultValues_AreCorrect()
    {
        // Act
        var score = new SecurityScore();

        // Assert
        score.Rating.Should().Be(SecurityRating.Good);
        score.TotalDevices.Should().Be(0);
        score.TotalPorts.Should().Be(0);
        score.DisabledPorts.Should().Be(0);
        score.MacRestrictedPorts.Should().Be(0);
        score.UnprotectedActivePorts.Should().Be(0);
        score.CriticalIssueCount.Should().Be(0);
        score.WarningCount.Should().Be(0);
    }
}

public class NetworkInfoTests
{
    [Fact]
    public void GetDisplayName_NativeVlan_ShowsNativeIndicator()
    {
        // Arrange
        var network = new NetworkInfo { Name = "Default", VlanId = 1 };

        // Act
        var displayName = network.GetDisplayName();

        // Assert
        displayName.Should().Be("Default (1 - native)");
    }

    [Fact]
    public void GetDisplayName_NonNativeVlan_ShowsJustNumber()
    {
        // Arrange
        var network = new NetworkInfo { Name = "IoT", VlanId = 50 };

        // Act
        var displayName = network.GetDisplayName();

        // Assert
        displayName.Should().Be("IoT (50)");
    }

    [Theory]
    [InlineData(null, NetworkType.Other)]
    [InlineData("", NetworkType.Other)]
    [InlineData("home", NetworkType.Home)]
    [InlineData("HOME", NetworkType.Home)]
    [InlineData("iot", NetworkType.IoT)]
    [InlineData("IoT", NetworkType.IoT)]
    [InlineData("security", NetworkType.Security)]
    [InlineData("management", NetworkType.Management)]
    [InlineData("guest", NetworkType.Guest)]
    [InlineData("corporate", NetworkType.Corporate)]
    [InlineData("unknown", NetworkType.Other)]
    public void ParsePurpose_VariousPurposes_ReturnsCorrectType(string? purpose, NetworkType expected)
    {
        // Act
        var result = NetworkInfo.ParsePurpose(purpose);

        // Assert
        result.Should().Be(expected);
    }
}

public class SwitchDetailTests
{
    [Fact]
    public void TotalPorts_ReturnsPortCount()
    {
        // Arrange
        var sw = new SwitchDetail
        {
            Ports = new List<PortDetail>
            {
                new() { PortIndex = 1 },
                new() { PortIndex = 2 },
                new() { PortIndex = 3 }
            }
        };

        // Assert
        sw.TotalPorts.Should().Be(3);
    }

    [Fact]
    public void DisabledPorts_CountsCorrectly()
    {
        // Arrange
        var sw = new SwitchDetail
        {
            Ports = new List<PortDetail>
            {
                new() { PortIndex = 1, Forward = "disabled" },
                new() { PortIndex = 2, Forward = "native" },
                new() { PortIndex = 3, Forward = "disabled" }
            }
        };

        // Assert
        sw.DisabledPorts.Should().Be(2);
    }

    [Fact]
    public void MacRestrictedPorts_CountsCorrectly()
    {
        // Arrange
        var sw = new SwitchDetail
        {
            Ports = new List<PortDetail>
            {
                new() { PortIndex = 1, PortSecurityMacs = new List<string> { "aa:bb:cc:dd:ee:ff" } },
                new() { PortIndex = 2, PortSecurityMacs = new List<string>() },
                new() { PortIndex = 3, PortSecurityMacs = new List<string> { "11:22:33:44:55:66", "77:88:99:aa:bb:cc" } }
            }
        };

        // Assert
        sw.MacRestrictedPorts.Should().Be(2);
    }

    [Fact]
    public void UnprotectedActivePorts_CountsCorrectly()
    {
        // Arrange
        var sw = new SwitchDetail
        {
            Ports = new List<PortDetail>
            {
                // Unprotected active
                new() { PortIndex = 1, Forward = "native", IsUp = true, PortSecurityMacs = new List<string>(), IsUplink = false },
                // Has MAC restriction
                new() { PortIndex = 2, Forward = "native", IsUp = true, PortSecurityMacs = new List<string> { "aa:bb:cc:dd:ee:ff" }, IsUplink = false },
                // Not up
                new() { PortIndex = 3, Forward = "native", IsUp = false, PortSecurityMacs = new List<string>(), IsUplink = false },
                // Disabled
                new() { PortIndex = 4, Forward = "disabled", IsUp = true, PortSecurityMacs = new List<string>(), IsUplink = false },
                // Uplink
                new() { PortIndex = 5, Forward = "native", IsUp = true, PortSecurityMacs = new List<string>(), IsUplink = true }
            }
        };

        // Assert
        sw.UnprotectedActivePorts.Should().Be(1);
    }
}

public class AccessPointDetailTests
{
    [Fact]
    public void TotalClients_ReturnsClientCount()
    {
        // Arrange
        var ap = new AccessPointDetail
        {
            Clients = new List<WirelessClientDetail>
            {
                new() { DisplayName = "Client1" },
                new() { DisplayName = "Client2" }
            }
        };

        // Assert
        ap.TotalClients.Should().Be(2);
    }

    [Fact]
    public void IoTClients_CountsCorrectly()
    {
        // Arrange
        var ap = new AccessPointDetail
        {
            Clients = new List<WirelessClientDetail>
            {
                new() { DisplayName = "Phone", IsIoT = false },
                new() { DisplayName = "Smart Light", IsIoT = true },
                new() { DisplayName = "Thermostat", IsIoT = true }
            }
        };

        // Assert
        ap.IoTClients.Should().Be(2);
    }

    [Fact]
    public void CameraClients_CountsCorrectly()
    {
        // Arrange
        var ap = new AccessPointDetail
        {
            Clients = new List<WirelessClientDetail>
            {
                new() { DisplayName = "Phone", IsCamera = false },
                new() { DisplayName = "Camera 1", IsCamera = true },
                new() { DisplayName = "Camera 2", IsCamera = true }
            }
        };

        // Assert
        ap.CameraClients.Should().Be(2);
    }
}

public class PortDetailTests
{
    [Fact]
    public void MacRestrictionCount_ReturnsPortSecurityMacsCount()
    {
        // Arrange
        var port = new PortDetail
        {
            PortSecurityMacs = new List<string> { "aa:bb:cc:dd:ee:ff", "11:22:33:44:55:66" }
        };

        // Assert
        port.MacRestrictionCount.Should().Be(2);
    }

    [Theory]
    [InlineData("disabled", "Disabled", PortStatusType.Ok)]
    [InlineData("all", "Trunk", PortStatusType.Ok)]
    public void GetStatus_VariousForwardModes_ReturnsCorrectStatus(
        string forward, string expectedStatus, PortStatusType expectedType)
    {
        // Arrange
        var port = new PortDetail { Forward = forward, IsUp = true };

        // Act
        var (status, statusType) = port.GetStatus();

        // Assert
        status.Should().Be(expectedStatus);
        statusType.Should().Be(expectedType);
    }

    [Fact]
    public void GetStatus_NativeNoMac_ReturnsWarning()
    {
        // Arrange
        var port = new PortDetail
        {
            Forward = "native",
            IsUp = true,
            IsUplink = false,
            PortSecurityMacs = new List<string>()
        };

        // Act
        var (status, statusType) = port.GetStatus(supportsAcls: true);

        // Assert
        status.Should().Be("No MAC");
        statusType.Should().Be(PortStatusType.Warning);
    }

    [Fact]
    public void GetStatus_NativeWithMac_ReturnsOk()
    {
        // Arrange
        var port = new PortDetail
        {
            Forward = "native",
            IsUp = true,
            IsUplink = false,
            PortSecurityMacs = new List<string> { "aa:bb:cc:dd:ee:ff" }
        };

        // Act
        var (status, statusType) = port.GetStatus();

        // Assert
        status.Should().Be("OK");
        statusType.Should().Be(PortStatusType.Ok);
    }

    [Fact]
    public void GetStatus_NotUpNotDisabled_ReturnsOff()
    {
        // Arrange
        var port = new PortDetail { Forward = "native", IsUp = false };

        // Act
        var (status, statusType) = port.GetStatus();

        // Assert
        status.Should().Be("Off");
        statusType.Should().Be(PortStatusType.Ok);
    }

    [Fact]
    public void GetStatus_Uplink_ReturnsTrunk()
    {
        // Arrange
        var port = new PortDetail { Forward = "native", IsUp = true, IsUplink = true };

        // Act
        var (status, statusType) = port.GetStatus();

        // Assert
        status.Should().Be("Trunk");
        statusType.Should().Be(PortStatusType.Ok);
    }

    [Fact]
    public void GetStatus_CustomWithApName_ReturnsAP()
    {
        // Arrange
        var port = new PortDetail { Forward = "custom", IsUp = true, Name = "AP Office" };

        // Act
        var (status, statusType) = port.GetStatus();

        // Assert
        status.Should().Be("AP");
        statusType.Should().Be(PortStatusType.Ok);
    }

    [Fact]
    public void GetStatus_IoTDeviceOnWrongVlan_ReturnsWarning()
    {
        // Arrange - An IKEA device on corporate VLAN (not IoT)
        var port = new PortDetail
        {
            Name = "IKEA Smart Light",
            Forward = "native",
            IsUp = true,
            NativeNetwork = "Corporate"
        };

        // Act
        var (status, statusType) = port.GetStatus();

        // Assert
        status.Should().Be("Possible Wrong VLAN");
        statusType.Should().Be(PortStatusType.Warning);
    }

    [Fact]
    public void GetStatus_IoTDeviceOnIoTVlan_ReturnsOk()
    {
        // Arrange - An IKEA device on IoT VLAN (correct)
        var port = new PortDetail
        {
            Name = "IKEA Smart Light",
            Forward = "native",
            IsUp = true,
            NativeNetwork = "IoT Network",
            PortSecurityMacs = new List<string> { "aa:bb:cc:dd:ee:ff" }
        };

        // Act
        var (status, statusType) = port.GetStatus();

        // Assert
        status.Should().Be("OK");
        statusType.Should().Be(PortStatusType.Ok);
    }
}

public class AuditIssueTests
{
    [Fact]
    public void GetDeviceDisplay_WirelessIssue_ReturnsClientName()
    {
        // Arrange
        var issue = new AuditIssue
        {
            IsWireless = true,
            ClientName = "Smart Thermostat",
            ClientMac = "aa:bb:cc:dd:ee:ff"
        };

        // Act
        var display = issue.GetDeviceDisplay();

        // Assert
        display.Should().Be("Smart Thermostat");
    }

    [Fact]
    public void GetDeviceDisplay_WirelessNoName_ReturnsClientMac()
    {
        // Arrange
        var issue = new AuditIssue
        {
            IsWireless = true,
            ClientName = null,
            ClientMac = "aa:bb:cc:dd:ee:ff"
        };

        // Act
        var display = issue.GetDeviceDisplay();

        // Assert
        display.Should().Be("aa:bb:cc:dd:ee:ff");
    }

    [Fact]
    public void GetDeviceDisplay_WiredWithOnPattern_ReturnsClientPart()
    {
        // Arrange
        var issue = new AuditIssue
        {
            IsWireless = false,
            SwitchName = "Smart Light on Office Switch"
        };

        // Act
        var display = issue.GetDeviceDisplay();

        // Assert
        display.Should().Be("Smart Light");
    }

    [Fact]
    public void GetDeviceDisplay_WiredNoOnPattern_ReturnsSwitchName()
    {
        // Arrange
        var issue = new AuditIssue
        {
            IsWireless = false,
            SwitchName = "Office Switch"
        };

        // Act
        var display = issue.GetDeviceDisplay();

        // Assert
        display.Should().Be("Office Switch");
    }

    [Fact]
    public void GetPortDisplay_WirelessWithBand_ReturnsApAndBand()
    {
        // Arrange
        var issue = new AuditIssue
        {
            IsWireless = true,
            AccessPoint = "Living Room AP",
            WifiBand = "5 GHz"
        };

        // Act
        var display = issue.GetPortDisplay();

        // Assert
        display.Should().Be("Living Room AP (5 GHz)");
    }

    [Fact]
    public void GetPortDisplay_WirelessNoBand_ReturnsApOnly()
    {
        // Arrange
        var issue = new AuditIssue
        {
            IsWireless = true,
            AccessPoint = "Living Room AP",
            WifiBand = null
        };

        // Act
        var display = issue.GetPortDisplay();

        // Assert
        display.Should().Be("Living Room AP");
    }

    [Fact]
    public void GetPortDisplay_WiredWithPortId_ReturnsPortIdAndName()
    {
        // Arrange
        var issue = new AuditIssue
        {
            IsWireless = false,
            PortId = "WAN1",
            PortName = "Internet"
        };

        // Act
        var display = issue.GetPortDisplay();

        // Assert
        display.Should().Be("WAN1 (Internet)");
    }

    [Fact]
    public void GetPortDisplay_WiredWithPortIndex_ReturnsPortInfo()
    {
        // Arrange
        var issue = new AuditIssue
        {
            IsWireless = false,
            PortIndex = 5,
            PortName = "Printer"
        };

        // Act
        var display = issue.GetPortDisplay();

        // Assert
        display.Should().Be("5 (Printer)");
    }

    [Fact]
    public void GetPortDisplay_WiredWithOnPattern_IncludesSwitchPart()
    {
        // Arrange
        var issue = new AuditIssue
        {
            IsWireless = false,
            SwitchName = "Device on Office Switch",
            PortIndex = 5,
            PortName = "Device"
        };

        // Act
        var display = issue.GetPortDisplay();

        // Assert
        display.Should().Contain("Office Switch");
    }
}

public class DnsSecuritySummaryTests
{
    #region GetDnsLeakProtectionDetail Tests

    [Fact]
    public void GetDnsLeakProtectionDetail_NoProtection_ReturnsCanBypass()
    {
        // Arrange
        var dns = new DnsSecuritySummary
        {
            DnsLeakProtection = false,
            HasDns53BlockRule = false,
            DnatProvidesFullCoverage = false
        };

        // Act
        var detail = dns.GetDnsLeakProtectionDetail();

        // Assert
        detail.Should().Be("Devices can bypass network DNS");
    }

    [Fact]
    public void GetDnsLeakProtectionDetail_Dns53Only_ReturnsBlocked()
    {
        // Arrange
        var dns = new DnsSecuritySummary
        {
            DnsLeakProtection = true,
            HasDns53BlockRule = true,
            DnatProvidesFullCoverage = false
        };

        // Act
        var detail = dns.GetDnsLeakProtectionDetail();

        // Assert
        detail.Should().Be("External DNS queries blocked");
    }

    [Fact]
    public void GetDnsLeakProtectionDetail_DnatOnly_ReturnsRedirected()
    {
        // Arrange
        var dns = new DnsSecuritySummary
        {
            DnsLeakProtection = true,
            HasDns53BlockRule = false,
            DnatProvidesFullCoverage = true
        };

        // Act
        var detail = dns.GetDnsLeakProtectionDetail();

        // Assert
        detail.Should().Be("External DNS queries redirected");
    }

    [Fact]
    public void GetDnsLeakProtectionDetail_BothProtections_ReturnsRedirectedAndBlocked()
    {
        // Arrange
        var dns = new DnsSecuritySummary
        {
            DnsLeakProtection = true,
            HasDns53BlockRule = true,
            DnatProvidesFullCoverage = true
        };

        // Act
        var detail = dns.GetDnsLeakProtectionDetail();

        // Assert
        detail.Should().Be("External DNS queries redirected and leakage blocked");
    }

    #endregion
}

public class PortSecuritySummaryTests
{
    [Fact]
    public void ProtectionPercentage_CalculatesCorrectly()
    {
        // Arrange
        var summary = new PortSecuritySummary
        {
            TotalPorts = 24,
            DisabledPorts = 10,
            MacRestrictedPorts = 8
        };

        // Act
        var percentage = summary.ProtectionPercentage;

        // Assert
        percentage.Should().BeApproximately(75.0, 0.1); // (10 + 8) / 24 * 100 = 75%
    }

    [Fact]
    public void ProtectionPercentage_ZeroPorts_ReturnsZero()
    {
        // Arrange
        var summary = new PortSecuritySummary { TotalPorts = 0 };

        // Act
        var percentage = summary.ProtectionPercentage;

        // Assert
        percentage.Should().Be(0);
    }

    [Fact]
    public void ProtectionPercentage_FullProtection_Returns100()
    {
        // Arrange
        var summary = new PortSecuritySummary
        {
            TotalPorts = 24,
            DisabledPorts = 12,
            MacRestrictedPorts = 12
        };

        // Act
        var percentage = summary.ProtectionPercentage;

        // Assert
        percentage.Should().Be(100);
    }
}
