using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class WirelessCameraVlanRuleTests
{
    private readonly WirelessCameraVlanRule _rule;

    public WirelessCameraVlanRuleTests()
    {
        _rule = new WirelessCameraVlanRule();
    }

    #region Rule Properties

    [Fact]
    public void RuleId_ReturnsExpectedValue()
    {
        _rule.RuleId.Should().Be("WIFI-CAMERA-VLAN-001");
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

    #region Evaluate Tests - Non-Surveillance Devices Should Be Ignored

    [Fact]
    public void Evaluate_DesktopDevice_ReturnsNull()
    {
        // Arrange
        var client = CreateWirelessClient(ClientDeviceCategory.Desktop);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SmartPlugDevice_ReturnsNull()
    {
        // Arrange - IoT but not surveillance
        var client = CreateWirelessClient(ClientDeviceCategory.SmartPlug);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(client, networks);

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
        var client = CreateWirelessClient(ClientDeviceCategory.Camera, network: securityNetwork);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SecuritySystemOnSecurityVlan_ReturnsNull()
    {
        // Arrange
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var client = CreateWirelessClient(ClientDeviceCategory.SecuritySystem, network: securityNetwork);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Camera on Wrong VLAN

    [Fact]
    public void Evaluate_CameraOnCorporateVlan_ReturnsIssue()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(ClientDeviceCategory.Camera, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("WIFI-CAMERA-VLAN-001");
        result.Severity.Should().Be(AuditSeverity.Critical);
        result.ScoreImpact.Should().Be(8);
        result.IsWireless.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_CameraOnIoTVlan_ReturnsIssue()
    {
        // Arrange - Cameras should be on Security, not IoT
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var client = CreateWirelessClient(ClientDeviceCategory.Camera, network: iotNetwork);
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void Evaluate_SecuritySystemOnGuestVlan_ReturnsIssue()
    {
        // Arrange
        var guestNetwork = new NetworkInfo { Id = "guest-net", Name = "Guest", VlanId = 50, Purpose = NetworkPurpose.Guest };
        var client = CreateWirelessClient(ClientDeviceCategory.SecuritySystem, network: guestNetwork);
        var networks = CreateNetworkList(guestNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
    }

    #endregion

    #region Evaluate Tests - Issue Details

    [Fact]
    public void Evaluate_IssueIncludesClientDetails()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(
            ClientDeviceCategory.Camera,
            network: corpNetwork,
            clientName: "Front Porch Camera",
            clientMac: "AA:BB:CC:DD:EE:FF",
            apName: "AP-Outdoor");
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.ClientName.Should().Be("Front Porch Camera");
        result.ClientMac.Should().Be("AA:BB:CC:DD:EE:FF");
        result.AccessPoint.Should().Be("AP-Outdoor");
        result.CurrentNetwork.Should().Be("Corporate");
        result.CurrentVlan.Should().Be(10);
    }

    [Fact]
    public void Evaluate_IssueRecommendsSecurityNetwork()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Cameras", VlanId = 30, Purpose = NetworkPurpose.Security };
        var client = CreateWirelessClient(ClientDeviceCategory.Camera, network: corpNetwork);
        var networks = new List<NetworkInfo> { corpNetwork, securityNetwork };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.RecommendedNetwork.Should().Be("Cameras");
        result.RecommendedVlan.Should().Be(30);
    }

    [Fact]
    public void Evaluate_IssueIncludesMetadata()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(ClientDeviceCategory.Camera, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("device_category");
        result.Metadata!["device_category"].Should().Be("Camera");
    }

    #endregion

    #region Evaluate Tests - Cloud Surveillance Devices Skipped

    [Fact]
    public void Evaluate_CloudCameraDevice_ReturnsNull()
    {
        // Arrange - Cloud cameras (Ring, Nest, etc.) are handled by IoT rules, not camera rules
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(ClientDeviceCategory.CloudCamera, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert - Cloud surveillance is skipped by this rule
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_CloudSecuritySystemDevice_ReturnsNull()
    {
        // Arrange - Cloud security systems are handled by IoT rules
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(ClientDeviceCategory.CloudSecuritySystem, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert - Cloud surveillance is skipped by this rule
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Null Network

    [Fact]
    public void Evaluate_ClientWithNullNetwork_ReturnsNull()
    {
        // Arrange - Client has no network assigned
        var client = CreateWirelessClient(ClientDeviceCategory.Camera, network: null);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert - Should skip when network is null
        result.Should().BeNull();
    }

    #endregion

    #region Helper Methods

    private static WirelessClientInfo CreateWirelessClient(
        ClientDeviceCategory category,
        NetworkInfo? network = null,
        string clientName = "Test Camera",
        string clientMac = "00:11:22:33:44:55",
        string? apName = null)
    {
        var client = new UniFiClientResponse
        {
            Mac = clientMac,
            Name = clientName,
            IsWired = false,
            NetworkId = network?.Id ?? string.Empty
        };

        var detection = new DeviceDetectionResult
        {
            Category = category,
            Source = DetectionSource.UniFiFingerprint,
            ConfidenceScore = 90
        };

        return new WirelessClientInfo
        {
            Client = client,
            Network = network,
            Detection = detection,
            AccessPointName = apName
        };
    }

    private static List<NetworkInfo> CreateNetworkList(params NetworkInfo[] networks)
    {
        if (networks.Length == 0)
        {
            return new List<NetworkInfo>
            {
                new() { Id = "default", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate }
            };
        }
        return networks.ToList();
    }

    #endregion
}
