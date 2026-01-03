using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class CameraVlanRuleTests
{
    private readonly CameraVlanRule _rule;
    private readonly DeviceTypeDetectionService _detectionService;

    public CameraVlanRuleTests()
    {
        _rule = new CameraVlanRule();
        _detectionService = new DeviceTypeDetectionService();
        _rule.SetDetectionService(_detectionService);
    }

    #region Rule Properties

    [Fact]
    public void RuleId_ReturnsExpectedValue()
    {
        _rule.RuleId.Should().Be("CAMERA-VLAN-001");
    }

    [Fact]
    public void RuleName_ReturnsExpectedValue()
    {
        _rule.RuleName.Should().Be("Camera VLAN Placement");
    }

    [Fact]
    public void Severity_IsCritical()
    {
        _rule.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void ScoreImpact_Is8()
    {
        _rule.ScoreImpact.Should().Be(8);
    }

    #endregion

    #region Evaluate Tests - Non-Camera Devices Should Be Ignored

    [Fact]
    public void Evaluate_DesktopDevice_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(portName: "Workstation", deviceCategory: ClientDeviceCategory.Desktop);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SmartPlugDevice_ReturnsNull()
    {
        // Arrange - IoT devices that are not cameras should be ignored
        var port = CreatePort(portName: "Smart Plug", deviceCategory: ClientDeviceCategory.SmartPlug);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_ServerDevice_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(portName: "File Server", deviceCategory: ClientDeviceCategory.Server);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_UnknownDevice_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(portName: "Unknown Device", deviceCategory: ClientDeviceCategory.Unknown);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Port State Checks

    [Fact]
    public void Evaluate_PortDown_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(portName: "Security Camera", isUp: false, deviceCategory: ClientDeviceCategory.Camera);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_TrunkPort_ReturnsNull()
    {
        // Arrange - Trunk ports should be ignored
        var port = CreatePort(portName: "Security Camera", forwardMode: "all", deviceCategory: ClientDeviceCategory.Camera);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_UplinkPort_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(portName: "Security Camera", isUplink: true, deviceCategory: ClientDeviceCategory.Camera);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WanPort_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(portName: "Security Camera", isWan: true, deviceCategory: ClientDeviceCategory.Camera);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Camera on Correct VLAN

    [Fact]
    public void Evaluate_CameraOnSecurityVlan_ReturnsNull()
    {
        // Arrange
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(portName: "Front Camera", deviceCategory: ClientDeviceCategory.Camera, networkId: securityNetwork.Id);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Camera on Wrong VLAN

    [Fact]
    public void Evaluate_CameraOnCorporateVlan_ReturnsCriticalIssue()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Backyard Camera", deviceCategory: ClientDeviceCategory.Camera, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("CAMERA-VLAN-001");
        result.Severity.Should().Be(AuditSeverity.Critical);
        result.ScoreImpact.Should().Be(8);
    }

    [Fact]
    public void Evaluate_CameraOnIoTVlan_ReturnsCriticalIssue()
    {
        // Arrange - Cameras should be on Security VLAN, not IoT VLAN
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var port = CreatePort(portName: "Garage Camera", deviceCategory: ClientDeviceCategory.Camera, networkId: iotNetwork.Id);
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void Evaluate_CameraOnGuestVlan_ReturnsCriticalIssue()
    {
        // Arrange
        var guestNetwork = new NetworkInfo { Id = "guest-net", Name = "Guest", VlanId = 50, Purpose = NetworkPurpose.Guest };
        var port = CreatePort(portName: "Lobby Camera", deviceCategory: ClientDeviceCategory.Camera, networkId: guestNetwork.Id);
        var networks = CreateNetworkList(guestNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Critical);
    }

    #endregion

    #region Evaluate Tests - Issue Details

    [Fact]
    public void Evaluate_IssueIncludesPortDetails()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portIndex: 8,
            portName: "Driveway Camera",
            deviceCategory: ClientDeviceCategory.Camera,
            networkId: corpNetwork.Id,
            switchName: "Garage Switch");
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Port.Should().Be("8");
        result.PortName.Should().Be("Driveway Camera");
        result.CurrentNetwork.Should().Be("Corporate");
        result.CurrentVlan.Should().Be(10);
    }

    [Fact]
    public void Evaluate_IssueRecommendsSecurityNetwork()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Cameras", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(portName: "Front Door Camera", deviceCategory: ClientDeviceCategory.Camera, networkId: corpNetwork.Id);
        var networks = new List<NetworkInfo> { corpNetwork, securityNetwork };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.RecommendedNetwork.Should().Be("Cameras");
        result.RecommendedVlan.Should().Be(30);
        result.RecommendedAction.Should().Contain("Cameras");
    }

    [Fact]
    public void Evaluate_IssueIncludesMetadata()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "PTZ Camera", deviceCategory: ClientDeviceCategory.Camera, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("device_category");
        result.Metadata!["device_category"].Should().Be("Camera");
        result.Metadata.Should().ContainKey("current_network_purpose");
        result.Metadata["current_network_purpose"].Should().Be("Corporate");
    }

    [Fact]
    public void Evaluate_IssueDeviceNameIncludesSwitchContext()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Backyard Camera",
            deviceCategory: ClientDeviceCategory.Camera,
            networkId: corpNetwork.Id,
            switchName: "Outdoor Switch",
            connectedClientName: "Reolink Camera");
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.DeviceName.Should().Contain("Outdoor Switch");
    }

    #endregion

    #region Down Port with MAC Restriction Tests

    [Fact]
    public void Evaluate_DownPortWithoutMacRestriction_ReturnsNull()
    {
        // Arrange - Down port without any MAC restrictions
        var port = CreatePort(
            portName: "Camera Port",
            isUp: false,
            networkId: "corp-net");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should skip down ports without MAC restrictions
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DownPortWithCameraMacRestriction_OnCorporateVlan_ReturnsIssue()
    {
        // Arrange - Down port with MAC restriction for an Amcrest camera
        // Amcrest MAC prefix: 9C:8E:CD
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Front Door Camera",
            isUp: false,
            networkId: corpNetwork.Id,
            allowedMacAddresses: new List<string> { "9C:8E:CD:11:22:33" });
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should detect camera from MAC OUI and flag VLAN issue
        result.Should().NotBeNull();
        result!.Message.Should().Contain("offline, MAC restricted");
        result.CurrentNetwork.Should().Be("Corporate");
    }

    [Fact]
    public void Evaluate_DownPortWithCameraMacRestriction_OnSecurityVlan_ReturnsNull()
    {
        // Arrange - Down port with MAC restriction for camera, correctly on Security VLAN
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(
            portName: "Front Door Camera",
            isUp: false,
            networkId: securityNetwork.Id,
            allowedMacAddresses: new List<string> { "9C:8E:CD:11:22:33" });
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Correctly placed, no issue
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DownPortWithNonCameraMacRestriction_ReturnsNull()
    {
        // Arrange - Down port with MAC restriction for non-camera device
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Device Port",
            isUp: false,
            networkId: corpNetwork.Id,
            allowedMacAddresses: new List<string> { "00:17:88:11:22:33" }); // Philips Hue (IoT, not camera)
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Not a camera device, should be ignored by camera rule
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DownPortWithCameraPortName_OnCorporateVlan_ReturnsIssue()
    {
        // Arrange - Down port with camera-indicating port name and MAC restriction
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Backyard Camera",
            isUp: false,
            networkId: corpNetwork.Id,
            allowedMacAddresses: new List<string> { "aa:bb:cc:dd:ee:ff" }); // Unknown vendor
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should detect from port name pattern
        result.Should().NotBeNull();
        result!.Message.Should().Contain("offline, MAC restricted");
    }

    [Fact]
    public void Evaluate_DownPortWithMacRestriction_DeviceNameUsesPortName()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Garage Camera",
            isUp: false,
            networkId: corpNetwork.Id,
            switchName: "Outdoor Switch",
            allowedMacAddresses: new List<string> { "9C:8E:CD:11:22:33" });
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Device name should use port name since no connected client
        result.Should().NotBeNull();
        result!.DeviceName.Should().Be("Garage Camera on Outdoor Switch");
    }

    [Fact]
    public void Evaluate_DownPortWithLastConnectionMac_CameraDevice_OnCorporateVlan_ReturnsIssue()
    {
        // Arrange - Down port with last_connection.mac for an Amcrest camera
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Driveway Camera",
            isUp: false,
            networkId: corpNetwork.Id,
            lastConnectionMac: "9C:8E:CD:11:22:33"); // Amcrest camera MAC
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should detect camera from last connection MAC
        result.Should().NotBeNull();
        result!.Message.Should().Contain("offline");
        result.CurrentNetwork.Should().Be("Corporate");
    }

    [Fact]
    public void Evaluate_DownPortWithLastConnectionMac_OnSecurityVlan_ReturnsNull()
    {
        // Arrange - Down port with last connection MAC, correctly on Security VLAN
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(
            portName: "Driveway Camera",
            isUp: false,
            networkId: securityNetwork.Id,
            lastConnectionMac: "9C:8E:CD:11:22:33");
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Correctly placed, no issue
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DownPortWithLastConnectionMac_NonCameraDevice_ReturnsNull()
    {
        // Arrange - Down port with last connection MAC for non-camera device (Philips Hue)
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Light Port",
            isUp: false,
            networkId: corpNetwork.Id,
            lastConnectionMac: "00:17:88:11:22:33"); // Philips Hue (IoT, not camera)
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Not a camera device
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DownPortWithNoMacInfo_ReturnsNull()
    {
        // Arrange - Down port with no last connection MAC and no MAC restrictions
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Empty Port",
            isUp: false,
            networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - No MAC info, should skip
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_UpPortNoClient_WithLastConnectionMac_CameraDevice_OnCorporateVlan_ReturnsIssue()
    {
        // Arrange - Port is UP (link active) but no client connected (camera in standby/offline)
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Driveway Camera",
            IsUp = true, // Port is UP (link active)
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = null, // No connected client (camera offline)
            LastConnectionMac = "9C:8E:CD:11:22:33" // Hikvision MAC
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should detect camera from last connection MAC even though port is UP
        result.Should().NotBeNull();
        result!.Message.Should().Contain("offline");
        result.CurrentNetwork.Should().Be("Corporate");
    }

    [Fact]
    public void Evaluate_UpPortNoClient_WithMacRestriction_CameraDevice_OnCorporateVlan_ReturnsIssue()
    {
        // Arrange - Port is UP but no client connected, has MAC restriction for camera
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Camera Port",
            IsUp = true, // Port is UP
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = null, // No connected client
            AllowedMacAddresses = new List<string> { "9C:8E:CD:44:55:66" } // Hikvision MAC
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should detect camera from MAC restriction
        result.Should().NotBeNull();
        result!.Message.Should().Contain("offline");
    }

    #endregion

    #region Helper Methods

    private static PortInfo CreatePort(
        int portIndex = 1,
        string? portName = null,
        bool isUp = true,
        string forwardMode = "native",
        bool isUplink = false,
        bool isWan = false,
        string? networkId = "default-net",
        string switchName = "Test Switch",
        ClientDeviceCategory deviceCategory = ClientDeviceCategory.Unknown,
        string? connectedClientName = null,
        List<string>? allowedMacAddresses = null,
        string? lastConnectionMac = null)
    {
        var switchInfo = new SwitchInfo
        {
            Name = switchName,
            Model = "USW-24",
            Type = "usw"
        };

        // Map category to a name pattern that will be detected
        var clientName = connectedClientName ?? GetDetectableName(deviceCategory, portName);

        UniFiClientResponse? connectedClient = null;
        if (isUp && (deviceCategory != ClientDeviceCategory.Unknown || clientName != null))
        {
            connectedClient = new UniFiClientResponse
            {
                Mac = "00:11:22:33:44:55",
                Name = clientName,
                IsWired = true,
                NetworkId = networkId
            };
        }

        return new PortInfo
        {
            PortIndex = portIndex,
            Name = portName,
            IsUp = isUp,
            ForwardMode = forwardMode,
            IsUplink = isUplink,
            IsWan = isWan,
            NativeNetworkId = networkId,
            Switch = switchInfo,
            ConnectedClient = connectedClient,
            AllowedMacAddresses = allowedMacAddresses,
            LastConnectionMac = lastConnectionMac
        };
    }

    /// <summary>
    /// Get a device name that will be detected as the given category by the NamePatternDetector
    /// </summary>
    private static string? GetDetectableName(ClientDeviceCategory category, string? fallback)
    {
        return category switch
        {
            ClientDeviceCategory.Camera => "Security Camera",
            ClientDeviceCategory.SmartPlug => "Smart Plug",
            ClientDeviceCategory.Desktop => "Desktop PC",
            ClientDeviceCategory.Server => "Server",
            ClientDeviceCategory.Unknown => fallback,
            _ => fallback
        };
    }

    private static List<NetworkInfo> CreateNetworkList(params NetworkInfo[] networks)
    {
        if (networks.Length == 0)
        {
            return new List<NetworkInfo>
            {
                new() { Id = "default-net", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate }
            };
        }
        return networks.ToList();
    }

    #endregion
}
