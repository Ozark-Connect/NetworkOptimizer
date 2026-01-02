using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class IotVlanRuleTests
{
    private readonly IotVlanRule _rule;
    private readonly DeviceTypeDetectionService _detectionService;

    public IotVlanRuleTests()
    {
        _rule = new IotVlanRule();
        _detectionService = new DeviceTypeDetectionService();
        _rule.SetDetectionService(_detectionService);
    }

    #region Rule Properties

    [Fact]
    public void RuleId_ReturnsExpectedValue()
    {
        _rule.RuleId.Should().Be("IOT-VLAN-001");
    }

    [Fact]
    public void RuleName_ReturnsExpectedValue()
    {
        _rule.RuleName.Should().Be("IoT Device VLAN Placement");
    }

    [Fact]
    public void Severity_IsCritical()
    {
        _rule.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void ScoreImpact_Is10()
    {
        _rule.ScoreImpact.Should().Be(10);
    }

    #endregion

    #region Evaluate Tests - Non-IoT Devices Should Be Ignored

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
    public void Evaluate_LaptopDevice_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(portName: "Laptop", deviceCategory: ClientDeviceCategory.Laptop);
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
        var port = CreatePort(portName: "Unknown", deviceCategory: ClientDeviceCategory.Unknown);
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
        var port = CreatePort(portName: "Smart Hub", isUp: false, deviceCategory: ClientDeviceCategory.SmartHub);
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
        var port = CreatePort(portName: "Smart Hub", forwardMode: "all", deviceCategory: ClientDeviceCategory.SmartHub);
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
        var port = CreatePort(portName: "Smart Hub", isUplink: true, deviceCategory: ClientDeviceCategory.SmartHub);
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
        var port = CreatePort(portName: "Smart Hub", isWan: true, deviceCategory: ClientDeviceCategory.SmartHub);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - IoT on Correct VLAN

    [Fact]
    public void Evaluate_SmartPlugOnIoTVlan_ReturnsNull()
    {
        // Arrange
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var port = CreatePort(portName: "Smart Plug", deviceCategory: ClientDeviceCategory.SmartPlug, networkId: iotNetwork.Id);
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SmartHubOnSecurityVlan_ReturnsNull()
    {
        // Arrange - Security VLAN is also acceptable for IoT isolation
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(portName: "Smart Hub", deviceCategory: ClientDeviceCategory.SmartHub, networkId: securityNetwork.Id);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SmartThermostatOnIoTVlan_ReturnsNull()
    {
        // Arrange
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT Devices", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var port = CreatePort(portName: "Thermostat", deviceCategory: ClientDeviceCategory.SmartThermostat, networkId: iotNetwork.Id);
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - IoT on Wrong VLAN

    [Fact]
    public void Evaluate_SmartPlugOnCorporateVlan_ReturnsRecommendedIssue()
    {
        // Arrange - SmartPlug is a low-risk IoT device, so severity is Recommended
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Smart Plug", deviceCategory: ClientDeviceCategory.SmartPlug, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("IOT-VLAN-001");
        result.Severity.Should().Be(AuditSeverity.Recommended); // Low-risk IoT device
        result.ScoreImpact.Should().Be(3); // Lower impact for low-risk devices
    }

    [Fact]
    public void Evaluate_SmartThermostatOnCorporateVlan_ReturnsCriticalIssue()
    {
        // Arrange - SmartThermostat is high-risk (control device)
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Thermostat", deviceCategory: ClientDeviceCategory.SmartThermostat, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("IOT-VLAN-001");
        result.Severity.Should().Be(AuditSeverity.Critical);
        result.ScoreImpact.Should().Be(10);
    }

    [Fact]
    public void Evaluate_SmartLockOnGuestVlan_ReturnsCriticalIssue()
    {
        // Arrange - SmartLock is high-risk (security device)
        var guestNetwork = new NetworkInfo { Id = "guest-net", Name = "Guest", VlanId = 50, Purpose = NetworkPurpose.Guest };
        var port = CreatePort(portName: "Front Door Lock", deviceCategory: ClientDeviceCategory.SmartLock, networkId: guestNetwork.Id);
        var networks = CreateNetworkList(guestNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Critical);
        result.ScoreImpact.Should().Be(10);
    }

    [Fact]
    public void Evaluate_SmartHubOnCorporateVlan_ReturnsCriticalIssue()
    {
        // Arrange - SmartHub is high-risk (control device)
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Smart Hub", deviceCategory: ClientDeviceCategory.SmartHub, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Critical);
    }

    #endregion

    #region Evaluate Tests - Low-Risk Media Devices Get Recommended Severity

    [Fact]
    public void Evaluate_SmartTVOnCorporateVlan_ReturnsRecommended()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Smart TV", deviceCategory: ClientDeviceCategory.SmartTV, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
    }

    [Fact]
    public void Evaluate_StreamingDeviceOnCorporateVlan_ReturnsRecommended()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Apple TV", deviceCategory: ClientDeviceCategory.StreamingDevice, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
    }

    [Fact]
    public void Evaluate_MediaPlayerOnCorporateVlan_ReturnsRecommended()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Media Player", deviceCategory: ClientDeviceCategory.MediaPlayer, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
    }

    [Fact]
    public void Evaluate_SmartSpeakerOnCorporateVlan_ReturnsRecommended()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Echo Dot", deviceCategory: ClientDeviceCategory.SmartSpeaker, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
    }

    [Fact]
    public void Evaluate_RoboticVacuumOnCorporateVlan_ReturnsRecommended()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Roomba", deviceCategory: ClientDeviceCategory.RoboticVacuum, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
    }

    #endregion

    #region Evaluate Tests - Issue Details

    [Fact]
    public void Evaluate_IssueIncludesPortDetails()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portIndex: 5,
            portName: "Living Room Plug",
            deviceCategory: ClientDeviceCategory.SmartPlug,
            networkId: corpNetwork.Id,
            switchName: "Office Switch");
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Port.Should().Be("5");
        result.PortName.Should().Be("Living Room Plug");
        result.CurrentNetwork.Should().Be("Corporate");
        result.CurrentVlan.Should().Be(10);
    }

    [Fact]
    public void Evaluate_IssueRecommendsIoTNetwork()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT Devices", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var port = CreatePort(portName: "Smart Plug", deviceCategory: ClientDeviceCategory.SmartPlug, networkId: corpNetwork.Id);
        var networks = new List<NetworkInfo> { corpNetwork, iotNetwork };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.RecommendedNetwork.Should().Be("IoT Devices");
        result.RecommendedVlan.Should().Be(40);
        result.RecommendedAction.Should().Contain("IoT Devices");
    }

    [Fact]
    public void Evaluate_IssueIncludesMetadata()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Smart Plug", deviceCategory: ClientDeviceCategory.SmartPlug, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("device_category");
        result.Metadata!["device_category"].Should().Be("SmartPlug");
        result.Metadata.Should().ContainKey("current_network_purpose");
        result.Metadata["current_network_purpose"].Should().Be("Corporate");
    }

    [Fact]
    public void Evaluate_IssueDeviceNameIncludesSwitchContext()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Smart Plug",
            deviceCategory: ClientDeviceCategory.SmartPlug,
            networkId: corpNetwork.Id,
            switchName: "Living Room Switch",
            connectedClientName: "My Smart Plug");
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.DeviceName.Should().Contain("Living Room Switch");
    }

    #endregion

    #region Down Port with MAC Restriction Tests

    [Fact]
    public void Evaluate_DownPortWithoutMacRestriction_ReturnsNull()
    {
        // Arrange - Down port without any MAC restrictions
        var port = CreatePort(
            portName: "Smart Plug",
            isUp: false,
            networkId: "corp-net");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should skip down ports without MAC restrictions
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DownPortWithIoTMacRestriction_OnCorporateVlan_ReturnsIssue()
    {
        // Arrange - Down port with MAC restriction for a Philips Hue device (IoT)
        // Philips Hue MAC prefix: 00:17:88
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Hue Bridge Port",
            isUp: false,
            networkId: corpNetwork.Id,
            allowedMacAddresses: new List<string> { "00:17:88:11:22:33" });
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should detect IoT device from MAC OUI and flag VLAN issue
        result.Should().NotBeNull();
        result!.Message.Should().Contain("port down, MAC restricted");
        result.CurrentNetwork.Should().Be("Corporate");
    }

    [Fact]
    public void Evaluate_DownPortWithIoTMacRestriction_OnIoTVlan_ReturnsNull()
    {
        // Arrange - Down port with MAC restriction for IoT device, correctly on IoT VLAN
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var port = CreatePort(
            portName: "Hue Bridge Port",
            isUp: false,
            networkId: iotNetwork.Id,
            allowedMacAddresses: new List<string> { "00:17:88:11:22:33" });
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Correctly placed, no issue
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DownPortWithNonIoTMacRestriction_ReturnsNull()
    {
        // Arrange - Down port with MAC restriction for non-IoT device (Apple)
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "MacBook Port",
            isUp: false,
            networkId: corpNetwork.Id,
            allowedMacAddresses: new List<string> { "00:03:93:11:22:33" }); // Apple MAC prefix
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Not an IoT device, should be ignored
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DownPortWithIoTPortName_OnCorporateVlan_ReturnsIssue()
    {
        // Arrange - Down port with IoT-indicating port name and MAC restriction
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Smart Thermostat",
            isUp: false,
            networkId: corpNetwork.Id,
            allowedMacAddresses: new List<string> { "aa:bb:cc:dd:ee:ff" }); // Unknown vendor
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should detect from port name pattern
        result.Should().NotBeNull();
        result!.Message.Should().Contain("port down, MAC restricted");
    }

    [Fact]
    public void Evaluate_DownPortWithMacRestriction_DeviceNameUsesPortName()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Living Room Plug",
            isUp: false,
            networkId: corpNetwork.Id,
            switchName: "Office Switch",
            allowedMacAddresses: new List<string> { "00:17:88:11:22:33" });
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Device name should use port name since no connected client
        result.Should().NotBeNull();
        result!.DeviceName.Should().Be("Living Room Plug on Office Switch");
    }

    [Fact]
    public void Evaluate_DownPortWithMacRestriction_NoPortName_UsesPortNumber()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portIndex: 5,
            portName: null,
            isUp: false,
            networkId: corpNetwork.Id,
            switchName: "Office Switch",
            allowedMacAddresses: new List<string> { "00:17:88:11:22:33" });
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Device name should use port number
        result.Should().NotBeNull();
        result!.DeviceName.Should().Be("Port 5 on Office Switch");
    }

    [Fact]
    public void Evaluate_DownPortWithLastConnectionMac_IoTDevice_OnCorporateVlan_ReturnsIssue()
    {
        // Arrange - Down port with last_connection.mac for a Philips Hue device
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Smart Light Port",
            isUp: false,
            networkId: corpNetwork.Id,
            lastConnectionMac: "00:17:88:11:22:33"); // Philips Hue MAC
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should detect IoT device from last connection MAC
        result.Should().NotBeNull();
        result!.Message.Should().Contain("port down");
        result.CurrentNetwork.Should().Be("Corporate");
    }

    [Fact]
    public void Evaluate_DownPortWithLastConnectionMac_OnIoTVlan_ReturnsNull()
    {
        // Arrange - Down port with last connection MAC, correctly on IoT VLAN
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var port = CreatePort(
            portName: "Smart Light Port",
            isUp: false,
            networkId: iotNetwork.Id,
            lastConnectionMac: "00:17:88:11:22:33");
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Correctly placed, no issue
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DownPortWithLastConnectionMac_NonIoTDevice_ReturnsNull()
    {
        // Arrange - Down port with last connection MAC for non-IoT device (Dell)
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "Workstation Port",
            isUp: false,
            networkId: corpNetwork.Id,
            lastConnectionMac: "00:14:22:11:22:33"); // Dell MAC prefix
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Not an IoT device
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DownPortWithBothLastConnectionAndMacRestriction_UsesHighestConfidence()
    {
        // Arrange - Down port with both last connection and MAC restriction
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(
            portName: "IoT Port",
            isUp: false,
            networkId: corpNetwork.Id,
            lastConnectionMac: "00:17:88:11:22:33", // Philips Hue (IoT)
            allowedMacAddresses: new List<string> { "00:17:88:44:55:66" }); // Also Philips Hue
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should detect IoT from either source
        result.Should().NotBeNull();
        result!.Message.Should().Contain("port down");
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
            ClientDeviceCategory.SmartPlug => "Smart Plug",
            ClientDeviceCategory.SmartThermostat => "Nest Thermostat",
            ClientDeviceCategory.SmartLock => "August Lock",
            ClientDeviceCategory.SmartHub => "SmartThings Hub",
            ClientDeviceCategory.SmartTV => "Samsung TV",
            ClientDeviceCategory.StreamingDevice => "Roku Ultra",
            ClientDeviceCategory.MediaPlayer => "Sonos One",
            ClientDeviceCategory.GameConsole => "Xbox One",
            ClientDeviceCategory.SmartSpeaker => "Echo Dot",
            ClientDeviceCategory.SmartLighting => "Philips Hue",
            ClientDeviceCategory.RoboticVacuum => "Roomba",
            ClientDeviceCategory.Camera => "Security Camera",
            ClientDeviceCategory.Desktop => "Desktop PC",
            ClientDeviceCategory.Laptop => "Laptop",
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
