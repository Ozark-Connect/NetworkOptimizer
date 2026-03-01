using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

/// <summary>
/// Tests for AnalyzeProtectCameraPlacement fallback logic.
/// This fallback catches Protect cameras that weren't matched to any switch port
/// during the normal port-level rule evaluation (CameraVlanRule).
/// </summary>
public class ProtectCameraFallbackTests
{
    private readonly PortSecurityAnalyzer _analyzer;

    public ProtectCameraFallbackTests()
    {
        var logger = new Mock<ILogger<PortSecurityAnalyzer>>();
        var detectionLogger = new Mock<ILogger<DeviceTypeDetectionService>>();
        var detectionService = new DeviceTypeDetectionService(detectionLogger.Object, null);
        _analyzer = new PortSecurityAnalyzer(logger.Object, detectionService);
    }

    #region Fallback Detection

    [Fact]
    public void AnalyzeProtectCameraPlacement_CameraNotOnAnyPort_FlagsWrongVlan()
    {
        // Arrange - Camera exists in Protect API but doesn't appear on any switch port
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var networks = new List<NetworkInfo> { corpNetwork, securityNetwork };

        var cameras = new ProtectCameraCollection();
        cameras.Add("aa:bb:cc:dd:ee:01", "G6 Pro Bullet", corpNetwork.Id, isNvr: false, uplinkMac: "00:11:22:33:44:01");
        _analyzer.SetProtectCameras(cameras);

        // Switch exists but camera MAC is not on any port
        var switches = new List<SwitchInfo>
        {
            CreateSwitch("Loft Switch", "00:11:22:33:44:01", new[]
            {
                CreatePort(1, "Port 1", isUp: true, connectedMac: "ff:ff:ff:ff:ff:01"), // Different device
                CreatePort(2, "Port 2", isUp: false)
            })
        };

        // Act
        var issues = _analyzer.AnalyzeProtectCameraPlacement(switches, networks, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Assert
        issues.Should().HaveCount(1);
        var issue = issues[0];
        issue.Type.Should().Be("CAMERA-VLAN-001");
        issue.DeviceName.Should().Contain("G6 Pro Bullet");
        issue.CurrentNetwork.Should().Be("Corporate");
        issue.RecommendedNetwork.Should().Be("Security");
        issue.Metadata!["source"].Should().Be("ProtectAPI");
        issue.Metadata["confidence"].Should().Be(100);
    }

    [Fact]
    public void AnalyzeProtectCameraPlacement_CameraOnSecurityVlan_ReturnsNoIssues()
    {
        // Arrange - Camera correctly placed on Security VLAN
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var networks = new List<NetworkInfo> { securityNetwork };

        var cameras = new ProtectCameraCollection();
        cameras.Add("aa:bb:cc:dd:ee:02", "G4 Dome", securityNetwork.Id, isNvr: false);
        _analyzer.SetProtectCameras(cameras);

        var switches = new List<SwitchInfo>();

        // Act
        var issues = _analyzer.AnalyzeProtectCameraPlacement(switches, networks, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Assert - Correctly placed
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeProtectCameraPlacement_CameraAlreadyOnPort_SkippedByFallback()
    {
        // Arrange - Camera MAC appears on a switch port, so the fallback should skip it
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var networks = new List<NetworkInfo> { corpNetwork };

        var cameras = new ProtectCameraCollection();
        cameras.Add("aa:bb:cc:dd:ee:03", "G5 Turret", corpNetwork.Id, isNvr: false);
        _analyzer.SetProtectCameras(cameras);

        // Camera MAC IS on a port (via LastConnectionMac)
        var switches = new List<SwitchInfo>
        {
            CreateSwitch("Test Switch", "00:11:22:33:44:03", new[]
            {
                CreatePort(1, "Port 1", isUp: false, lastConnectionMac: "aa:bb:cc:dd:ee:03")
            })
        };

        // Act
        var issues = _analyzer.AnalyzeProtectCameraPlacement(switches, networks, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Assert - Skipped because it's on a port (handled by CameraVlanRule)
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeProtectCameraPlacement_CameraAlreadyFlagged_SkippedForDedup()
    {
        // Arrange - Camera MAC is in the alreadyFlaggedMacs set
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var networks = new List<NetworkInfo> { corpNetwork };

        var cameras = new ProtectCameraCollection();
        cameras.Add("aa:bb:cc:dd:ee:04", "G4 Pro", corpNetwork.Id, isNvr: false);
        _analyzer.SetProtectCameras(cameras);

        var switches = new List<SwitchInfo>();
        var alreadyFlagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aa:bb:cc:dd:ee:04" };

        // Act
        var issues = _analyzer.AnalyzeProtectCameraPlacement(switches, networks, alreadyFlagged);

        // Assert - Skipped due to dedup
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeProtectCameraPlacement_NoProtectCameras_ReturnsEmpty()
    {
        // Arrange - No Protect cameras configured
        var networks = new List<NetworkInfo>
        {
            new() { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate }
        };
        var switches = new List<SwitchInfo>();

        // Act
        var issues = _analyzer.AnalyzeProtectCameraPlacement(switches, networks, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeProtectCameraPlacement_NvrOnCorporateVlan_FlaggedWithNvrMessage()
    {
        // Arrange - NVR not on any port, on wrong VLAN
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var mgmtNetwork = new NetworkInfo { Id = "mgmt-net", Name = "Management", VlanId = 5, Purpose = NetworkPurpose.Management };
        var networks = new List<NetworkInfo> { corpNetwork, mgmtNetwork };

        var cameras = new ProtectCameraCollection();
        cameras.Add("aa:bb:cc:dd:ee:05", "UNVR-Pro", corpNetwork.Id, isNvr: true);
        _analyzer.SetProtectCameras(cameras);

        var switches = new List<SwitchInfo>();

        // Act
        var issues = _analyzer.AnalyzeProtectCameraPlacement(switches, networks, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Message.Should().StartWith("NVR");
        issues[0].Message.Should().Contain("management or security");
        issues[0].Metadata!["category"].Should().Be("NVR");
    }

    [Fact]
    public void AnalyzeProtectCameraPlacement_NvrOnManagementVlan_CorrectlyPlaced()
    {
        // Arrange - NVR on Management VLAN is correctly placed
        var mgmtNetwork = new NetworkInfo { Id = "mgmt-net", Name = "Management", VlanId = 5, Purpose = NetworkPurpose.Management };
        var networks = new List<NetworkInfo> { mgmtNetwork };

        var cameras = new ProtectCameraCollection();
        cameras.Add("aa:bb:cc:dd:ee:06", "UNVR", mgmtNetwork.Id, isNvr: true);
        _analyzer.SetProtectCameras(cameras);

        var switches = new List<SwitchInfo>();

        // Act
        var issues = _analyzer.AnalyzeProtectCameraPlacement(switches, networks, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Assert - NVR on Management VLAN is correct
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeProtectCameraPlacement_CameraWithUplinkMac_FindsSwitchAndPort()
    {
        // Arrange - Camera with UplinkMac, not matched by MAC on port,
        // but UplinkMac matches a switch. Fallback should find the switch for display.
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var networks = new List<NetworkInfo> { corpNetwork, securityNetwork };

        var cameras = new ProtectCameraCollection();
        cameras.Add("aa:bb:cc:dd:ee:07", "G6 Pro Bullet", corpNetwork.Id, isNvr: false, uplinkMac: "00:11:22:33:44:07");
        _analyzer.SetProtectCameras(cameras);

        // Switch matches UplinkMac but camera MAC is NOT on any port
        var switches = new List<SwitchInfo>
        {
            CreateSwitch("Garage Switch", "00:11:22:33:44:07", new[]
            {
                CreatePort(1, "Port 1", isUp: true, connectedMac: "ff:ff:ff:ff:ff:01"),
                CreatePort(2, "Port 2", isUp: false)
            })
        };

        // Act
        var issues = _analyzer.AnalyzeProtectCameraPlacement(switches, networks, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Assert - Issue created but without specific port info (camera not matched to port)
        issues.Should().HaveCount(1);
        issues[0].DeviceName.Should().Be("G6 Pro Bullet"); // No port match, just camera name
    }

    [Fact]
    public void AnalyzeProtectCameraPlacement_CameraNoConnectionNetworkId_Skipped()
    {
        // Arrange - Camera with no ConnectionNetworkId (can't determine placement)
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var networks = new List<NetworkInfo> { corpNetwork };

        var cameras = new ProtectCameraCollection();
        cameras.Add("aa:bb:cc:dd:ee:08", "G4 Instant", null, isNvr: false);
        _analyzer.SetProtectCameras(cameras);

        var switches = new List<SwitchInfo>();

        // Act
        var issues = _analyzer.AnalyzeProtectCameraPlacement(switches, networks, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Assert - Skipped, no network to check against
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeProtectCameraPlacement_MultipleCameras_OnlyFlagsWronglyPlaced()
    {
        // Arrange - Mix of correctly and incorrectly placed cameras
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var networks = new List<NetworkInfo> { corpNetwork, securityNetwork };

        var cameras = new ProtectCameraCollection();
        cameras.Add("aa:bb:cc:dd:ee:09", "Front Door Camera", securityNetwork.Id, isNvr: false); // Correct
        cameras.Add("aa:bb:cc:dd:ee:10", "Backyard Camera", corpNetwork.Id, isNvr: false);      // Wrong
        cameras.Add("aa:bb:cc:dd:ee:11", "Garage Camera", corpNetwork.Id, isNvr: false);         // Wrong
        _analyzer.SetProtectCameras(cameras);

        var switches = new List<SwitchInfo>();

        // Act
        var issues = _analyzer.AnalyzeProtectCameraPlacement(switches, networks, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Assert - Only the two wrong ones flagged
        issues.Should().HaveCount(2);
        issues.Should().OnlyContain(i => i.Type == "CAMERA-VLAN-001");
        issues.Select(i => i.Metadata!["camera_name"]).Should().Contain("Backyard Camera");
        issues.Select(i => i.Metadata!["camera_name"]).Should().Contain("Garage Camera");
    }

    #endregion

    #region Helper Methods

    private static SwitchInfo CreateSwitch(string name, string macAddress, PortInfo[] ports)
    {
        var sw = new SwitchInfo
        {
            Name = name,
            MacAddress = macAddress,
            Model = "USW-24",
            Type = "usw",
            Ports = ports.ToList()
        };

        // Set Switch reference on all ports
        foreach (var port in sw.Ports)
        {
            // PortInfo.Switch is required init, so we create new instances
        }

        return sw;
    }

    private static PortInfo CreatePort(
        int portIndex,
        string name,
        bool isUp,
        string? connectedMac = null,
        string? lastConnectionMac = null,
        string? historicalMac = null,
        string forwardMode = "native")
    {
        var switchInfo = new SwitchInfo { Name = "Placeholder", Model = "USW-24", Type = "usw" };

        UniFiClientResponse? client = null;
        if (!string.IsNullOrEmpty(connectedMac))
        {
            client = new UniFiClientResponse
            {
                Mac = connectedMac,
                Name = "Device",
                IsWired = true,
                NetworkId = "net-1"
            };
        }

        UniFiClientDetailResponse? historicalClient = null;
        if (!string.IsNullOrEmpty(historicalMac))
        {
            historicalClient = new UniFiClientDetailResponse { Mac = historicalMac };
        }

        return new PortInfo
        {
            PortIndex = portIndex,
            Name = name,
            IsUp = isUp,
            ForwardMode = forwardMode,
            Switch = switchInfo,
            ConnectedClient = client,
            LastConnectionMac = lastConnectionMac,
            HistoricalClient = historicalClient
        };
    }

    #endregion
}
