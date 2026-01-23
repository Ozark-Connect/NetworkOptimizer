using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
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

    [Fact]
    public void Evaluate_ProtectCamera_DeviceNameUsesProductName()
    {
        // Arrange - Protect camera detected by MAC with no client name, should use ProductName from detection
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var protectCameras = new ProtectCameraCollection();
        protectCameras.Add("00:11:22:33:44:55", "G6 Pro Bullet");
        _detectionService.SetProtectCameras(protectCameras);

        var switchInfo = new SwitchInfo { Name = "Outdoor Switch", Model = "USW-24", Type = "usw" };
        var connectedClient = new UniFiClientResponse
        {
            Mac = "00:11:22:33:44:55",
            Name = null!,       // No name - testing null handling
            Hostname = null!,   // No hostname - testing null handling
            IsWired = true,
            NetworkId = corpNetwork.Id
        };

        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Port 1",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = connectedClient
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Device name should use ProductName from Protect detection
        result.Should().NotBeNull();
        result!.DeviceName.Should().Be("G6 Pro Bullet on Outdoor Switch");
    }

    [Fact]
    public void Evaluate_ClientWithNoName_FallsBackToOuiAndMac()
    {
        // Arrange - Client with no name but with OUI should fallback to "OUI (XX:XX)" format
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Outdoor Switch", Model = "USW-24", Type = "usw" };

        // Amcrest camera OUI (9C:8E:CD) without a name
        var connectedClient = new UniFiClientResponse
        {
            Mac = "9C:8E:CD:11:22:33",
            Name = null!,       // No name - testing null handling
            Hostname = null!,   // No hostname - testing null handling
            Oui = "Amcrest",    // Manufacturer
            IsWired = true,
            NetworkId = corpNetwork.Id
        };

        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Camera Port",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = connectedClient
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Device name should use OUI + MAC suffix format
        result.Should().NotBeNull();
        result!.DeviceName.Should().Be("Amcrest (22:33) on Outdoor Switch");
    }

    [Fact]
    public void Evaluate_ClientWithNoNameOrOui_FallsBackToVendorName()
    {
        // Arrange - Client with no name and no OUI, but MAC vendor is detected
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Outdoor Switch", Model = "USW-24", Type = "usw" };

        // Reolink camera MAC without OUI set - detection should find vendor from MAC OUI mapping
        var connectedClient = new UniFiClientResponse
        {
            Mac = "EC:71:DB:11:22:33", // Reolink MAC prefix
            Name = null!,       // No name
            Hostname = null!,   // No hostname
            Oui = null!,        // No OUI set - testing vendor fallback
            IsWired = true,
            NetworkId = corpNetwork.Id
        };

        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Camera Port",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = connectedClient
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Device name should use vendor from detection + MAC suffix
        result.Should().NotBeNull();
        result!.DeviceName.Should().Contain("Reolink");
        result.DeviceName.Should().Contain("Outdoor Switch");
    }

    [Fact]
    public void Evaluate_ClientWithNoNameOuiOrVendor_FallsBackToMac()
    {
        // Arrange - Client with absolutely no identifying info except MAC
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Outdoor Switch", Model = "USW-24", Type = "usw" };

        // Unknown MAC that still gets detected as camera via port name
        var connectedClient = new UniFiClientResponse
        {
            Mac = "AA:BB:CC:DD:EE:FF",
            Name = null!,
            Hostname = null!,
            Oui = null!,
            IsWired = true,
            NetworkId = corpNetwork.Id
        };

        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Security Camera", // This name pattern triggers camera detection
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = connectedClient
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Device name should fall back to MAC
        result.Should().NotBeNull();
        result!.DeviceName.Should().Contain("AA:BB:CC:DD:EE:FF");
        result.DeviceName.Should().Contain("Outdoor Switch");
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
        result!.CurrentNetwork.Should().Be("Corporate");
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

    }

    #endregion

    #region Cloud Camera Tests

    [Fact]
    public void Evaluate_CloudCameraOnCorporateVlan_ReturnsNull()
    {
        // Arrange - Cloud cameras (Ring, Nest, etc.) are handled by IoT rules, not camera rules
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Ring Camera", deviceCategory: ClientDeviceCategory.CloudCamera, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Cloud surveillance is skipped by this rule
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_CloudSecuritySystemOnCorporateVlan_ReturnsNull()
    {
        // Arrange - Cloud security systems are handled by IoT rules
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "SimpliSafe Base", deviceCategory: ClientDeviceCategory.CloudSecuritySystem, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Cloud surveillance is skipped by this rule
        result.Should().BeNull();
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
        string? lastConnectionMac = null,
        long? lastConnectionSeen = null)
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
                Name = clientName ?? string.Empty,
                IsWired = true,
                NetworkId = networkId ?? string.Empty
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
            LastConnectionMac = lastConnectionMac,
            LastConnectionSeen = lastConnectionSeen
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
            ClientDeviceCategory.CloudCamera => "Ring Camera",
            ClientDeviceCategory.CloudSecuritySystem => "SimpliSafe Hub",
            ClientDeviceCategory.SecuritySystem => "Security System",
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

    #region Cloud Camera Tests - Should Be Skipped By CameraVlanRule

    [Fact]
    public void Evaluate_CloudCameraDevice_ReturnsNull()
    {
        // Arrange - CloudCamera devices should be handled by IoT rules, not Camera rules
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Cloud Camera Port",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "0C:47:C9:11:22:33", // Ring MAC prefix
                Name = "Ring Doorbell",
                IsWired = true,
                NetworkId = corpNetwork.Id
            }
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Cloud cameras should be skipped (handled by IoT rules)
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_RingCamera_ReturnsNull()
    {
        // Arrange - Ring is a cloud camera, should be skipped
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Ring Camera",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "34:1F:4F:11:22:33", // Ring MAC prefix
                Name = "Ring Cam",
                IsWired = true,
                NetworkId = corpNetwork.Id
            }
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Ring cameras are cloud cameras, should be skipped
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NestCamera_ReturnsNull()
    {
        // Arrange - Nest/Google cameras are cloud cameras, should be skipped
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Nest Camera",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "18:B4:30:11:22:33", // Nest MAC prefix (detected as CloudCamera via name pattern)
                Name = "Nest Cam Indoor", // Name indicates camera
                IsWired = true,
                NetworkId = corpNetwork.Id
            }
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Nest cameras are cloud cameras, should be skipped
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_GoogleNestCamera_ByName_ReturnsNull()
    {
        // Arrange - Google Nest camera detected by name pattern
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Google Nest Camera",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "AA:BB:CC:11:22:33", // Unknown MAC, but name indicates Nest camera
                Name = "Nest Hello Doorbell",
                IsWired = true,
                NetworkId = corpNetwork.Id
            }
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Nest identified by name as cloud camera, should be skipped
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WyzeCamera_ReturnsNull()
    {
        // Arrange - Wyze is a cloud camera, should be skipped
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Wyze Cam",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "2C:AA:8E:11:22:33", // Wyze MAC prefix
                Name = "Wyze Cam v3",
                IsWired = true,
                NetworkId = corpNetwork.Id
            }
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Wyze cameras are cloud cameras, should be skipped
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_BlinkCamera_ReturnsNull()
    {
        // Arrange - Blink is a cloud camera (Amazon), should be skipped
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Blink Camera",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "9C:55:B4:11:22:33", // Blink MAC prefix
                Name = "Blink Outdoor",
                IsWired = true,
                NetworkId = corpNetwork.Id
            }
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Blink cameras are cloud cameras, should be skipped
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_ArloCamera_ReturnsNull()
    {
        // Arrange - Arlo is a cloud camera, should be skipped
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Arlo Pro",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "4C:77:6D:11:22:33", // Arlo MAC prefix
                Name = "Arlo Pro 4",
                IsWired = true,
                NetworkId = corpNetwork.Id
            }
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Arlo cameras are cloud cameras, should be skipped
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SelfHostedCamera_StillDetected()
    {
        // Arrange - Self-hosted cameras (e.g., Reolink) should still be flagged
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Reolink Camera",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "EC:71:DB:11:22:33", // Reolink MAC prefix
                Name = "Reolink RLC-810A",
                IsWired = true,
                NetworkId = corpNetwork.Id
            }
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Reolink is self-hosted, should be flagged
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void Evaluate_UniFiProtectCamera_StillDetected()
    {
        // Arrange - UniFi Protect cameras are self-hosted, should be flagged
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "UniFi Camera",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "FC:EC:DA:11:22:33", // UniFi Protect MAC prefix
                Name = "G4 Doorbell",
                IsWired = true,
                NetworkId = corpNetwork.Id
            }
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - UniFi Protect is self-hosted, should be flagged
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void Evaluate_EufyCamera_StillDetected()
    {
        // Arrange - Eufy cameras are self-hosted (local storage), should be flagged
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var switchInfo = new SwitchInfo { Name = "Test Switch", Model = "USW-24", Type = "usw" };
        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Eufy Camera",
            IsUp = true,
            ForwardMode = "native",
            NativeNetworkId = corpNetwork.Id,
            Switch = switchInfo,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "8C:85:80:11:22:33", // Eufy MAC prefix
                Name = "Eufy Cam 2C",
                IsWired = true,
                NetworkId = corpNetwork.Id
            }
        };
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Eufy is self-hosted, should be flagged
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Critical);
    }

    #endregion

    #region Offline Device 2-Week Scoring Tests

    [Fact]
    public void Evaluate_OfflineCamera_RecentlyActive_ReturnsCritical()
    {
        // Arrange - offline camera last seen 1 week ago (within 2-week window)
        var oneWeekAgo = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();
        var port = CreatePort(
            portName: "Security Camera",
            isUp: false,
            deviceCategory: ClientDeviceCategory.Camera,
            lastConnectionMac: "00:11:22:33:44:55",
            lastConnectionSeen: oneWeekAgo);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - recently active offline camera should still be Critical
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Critical);
        result.ScoreImpact.Should().Be(8); // CameraVlanRule uses ScoreImpact of 8
    }

    [Fact]
    public void Evaluate_OfflineCamera_StaleOlderThan2Weeks_ReturnsInformational()
    {
        // Arrange - offline camera last seen 3 weeks ago (outside 2-week window)
        var threeWeeksAgo = DateTimeOffset.UtcNow.AddDays(-21).ToUnixTimeSeconds();
        var port = CreatePort(
            portName: "Security Camera",
            isUp: false,
            deviceCategory: ClientDeviceCategory.Camera,
            lastConnectionMac: "00:11:22:33:44:55",
            lastConnectionSeen: threeWeeksAgo);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - stale offline camera should be Informational with no score impact
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(0);
    }

    [Fact]
    public void Evaluate_OfflineCamera_NoLastConnectionSeen_ReturnsInformational()
    {
        // Arrange - offline camera with no timestamp (treated as stale)
        var port = CreatePort(
            portName: "Security Camera",
            isUp: false,
            deviceCategory: ClientDeviceCategory.Camera,
            lastConnectionMac: "00:11:22:33:44:55",
            lastConnectionSeen: null);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - no timestamp means stale, should be Informational
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(0);
    }

    #endregion
}
