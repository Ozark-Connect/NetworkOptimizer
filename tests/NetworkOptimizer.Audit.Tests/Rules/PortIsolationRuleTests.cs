using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Core.Enums;
using Xunit;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class PortIsolationRuleTests
{
    private readonly PortIsolationRule _rule;

    public PortIsolationRuleTests()
    {
        _rule = new PortIsolationRule();
    }

    #region Rule Properties

    [Fact]
    public void RuleId_ReturnsExpectedValue()
    {
        _rule.RuleId.Should().Be("PORT-ISOLATION-001");
    }

    [Fact]
    public void RuleName_ReturnsExpectedValue()
    {
        _rule.RuleName.Should().Be("Port Isolation for Sensitive Devices");
    }

    [Fact]
    public void Severity_IsRecommended()
    {
        _rule.Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void ScoreImpact_Is4()
    {
        _rule.ScoreImpact.Should().Be(4);
    }

    #endregion

    #region Evaluate Tests - Port State Checks

    [Fact]
    public void Evaluate_PortDown_ReturnsNull()
    {
        // Arrange
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(portName: "Camera", isUp: false, supportsIsolation: true, networkId: securityNetwork.Id);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_TrunkPort_ReturnsNull()
    {
        // Arrange
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(portName: "Camera", forwardMode: "all", supportsIsolation: true, networkId: securityNetwork.Id);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_UplinkPort_ReturnsNull()
    {
        // Arrange
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(portName: "Camera", isUplink: true, supportsIsolation: true, networkId: securityNetwork.Id);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WanPort_ReturnsNull()
    {
        // Arrange
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(portName: "Camera", isWan: true, supportsIsolation: true, networkId: securityNetwork.Id);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Switch Capabilities

    [Fact]
    public void Evaluate_SwitchDoesNotSupportIsolation_ReturnsNull()
    {
        // Arrange
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(portName: "Camera", supportsIsolation: false, networkId: securityNetwork.Id);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Non-Sensitive Devices Ignored

    [Fact]
    public void Evaluate_RegularDevice_ReturnsNull()
    {
        // Arrange - Device name doesn't indicate camera or IoT
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Workstation", supportsIsolation: true, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_ServerDevice_ReturnsNull()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "File Server", supportsIsolation: true, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Camera on Security VLAN

    [Fact]
    public void Evaluate_CameraOnSecurityVlanWithIsolation_ReturnsNull()
    {
        // Arrange - Camera with isolation enabled is correctly configured
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(portName: "Camera", supportsIsolation: true, isolationEnabled: true, networkId: securityNetwork.Id);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_CameraOnSecurityVlanWithoutIsolation_ReturnsIssue()
    {
        // Arrange - Camera without isolation should report issue
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(portName: "Camera", supportsIsolation: true, isolationEnabled: false, networkId: securityNetwork.Id);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("PORT-ISOLATION-001");
        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(4);
        result.Message.Should().Contain("Camera");
        result.Message.Should().Contain("port isolation");
    }

    [Fact]
    public void Evaluate_NVROnSecurityVlanWithoutIsolation_ReturnsIssue()
    {
        // Arrange
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(portName: "NVR", supportsIsolation: true, isolationEnabled: false, networkId: securityNetwork.Id);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Message.Should().Contain("Camera");
    }

    [Fact]
    public void Evaluate_CameraOnWrongVlan_ReturnsNull()
    {
        // Arrange - Camera on corporate VLAN should be handled by CameraVlanRule, not this one
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Camera", supportsIsolation: true, isolationEnabled: false, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - IoT Device on IoT VLAN

    [Fact]
    public void Evaluate_IoTDeviceOnIoTVlanWithIsolation_ReturnsNull()
    {
        // Arrange - IoT device with isolation enabled is correctly configured
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var port = CreatePort(portName: "Smart Plug", supportsIsolation: true, isolationEnabled: true, networkId: iotNetwork.Id);
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_IoTDeviceOnIoTVlanWithoutIsolation_ReturnsIssue()
    {
        // Arrange - IoT device without isolation should report issue
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var port = CreatePort(portName: "Smart Plug", supportsIsolation: true, isolationEnabled: false, networkId: iotNetwork.Id);
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("PORT-ISOLATION-001");
        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.Message.Should().Contain("IoT device");
        result.Message.Should().Contain("port isolation");
    }

    [Fact]
    public void Evaluate_HueBridgeOnIoTVlanWithoutIsolation_ReturnsIssue()
    {
        // Arrange
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var port = CreatePort(portName: "Philips Hue Bridge", supportsIsolation: true, isolationEnabled: false, networkId: iotNetwork.Id);
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Message.Should().Contain("IoT device");
    }

    [Fact]
    public void Evaluate_IoTDeviceOnWrongVlan_ReturnsNull()
    {
        // Arrange - IoT device on corporate VLAN should be handled by IotVlanRule, not this one
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var port = CreatePort(portName: "Smart Plug", supportsIsolation: true, isolationEnabled: false, networkId: corpNetwork.Id);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Issue Details

    [Fact]
    public void Evaluate_IssueIncludesMetadata()
    {
        // Arrange
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Cameras", VlanId = 30, Purpose = NetworkPurpose.Security };
        var port = CreatePort(
            portIndex: 5,
            portName: "Front Camera",
            supportsIsolation: true,
            isolationEnabled: false,
            networkId: securityNetwork.Id,
            switchName: "Garage Switch");
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("device_type");
        result.Metadata!["device_type"].Should().Be("Camera");
        result.Metadata.Should().ContainKey("network");
        result.Metadata["network"].Should().Be("Cameras");
        result.Metadata.Should().ContainKey("recommendation");
    }

    [Fact]
    public void Evaluate_IssueIncludesPortDetails()
    {
        // Arrange
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT Devices", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var port = CreatePort(
            portIndex: 12,
            portName: "Nest Thermostat",
            supportsIsolation: true,
            isolationEnabled: false,
            networkId: iotNetwork.Id,
            switchName: "Hallway Switch");
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Port.Should().Be("12");
        result.PortName.Should().Be("Nest Thermostat");
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
        bool supportsIsolation = false,
        bool isolationEnabled = false)
    {
        var switchInfo = new SwitchInfo
        {
            Name = switchName,
            Model = "USW-24",
            Type = "usw",
            Capabilities = new SwitchCapabilities
            {
                SupportsIsolation = supportsIsolation
            }
        };

        return new PortInfo
        {
            PortIndex = portIndex,
            Name = portName,
            IsUp = isUp,
            ForwardMode = forwardMode,
            IsUplink = isUplink,
            IsWan = isWan,
            NativeNetworkId = networkId,
            IsolationEnabled = isolationEnabled,
            Switch = switchInfo
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
