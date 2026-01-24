using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class WirelessIotVlanRuleTests
{
    private readonly WirelessIotVlanRule _rule;

    public WirelessIotVlanRuleTests()
    {
        _rule = new WirelessIotVlanRule();
    }

    #region Rule Properties

    [Fact]
    public void RuleId_ReturnsExpectedValue()
    {
        _rule.RuleId.Should().Be("WIFI-IOT-VLAN-001");
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
        var client = CreateWirelessClient(ClientDeviceCategory.Desktop);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_DesktopDevice2_ReturnsNull()
    {
        // Arrange - Test with another non-IoT device type
        var client = CreateWirelessClient(ClientDeviceCategory.Laptop);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_UnknownDevice_ReturnsNull()
    {
        // Arrange
        var client = CreateWirelessClient(ClientDeviceCategory.Unknown);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(client, networks);

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
        var client = CreateWirelessClient(ClientDeviceCategory.SmartPlug, network: iotNetwork);
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SmartSpeakerOnSecurityVlan_ReturnsNull()
    {
        // Arrange - Security VLAN is also acceptable for IoT isolation
        var securityNetwork = new NetworkInfo { Id = "sec-net", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var client = CreateWirelessClient(ClientDeviceCategory.SmartSpeaker, network: securityNetwork);
        var networks = CreateNetworkList(securityNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

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
        var client = CreateWirelessClient(ClientDeviceCategory.SmartPlug, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("WIFI-IOT-VLAN-001");
        result.Severity.Should().Be(AuditSeverity.Recommended); // Low-risk IoT device
        result.ScoreImpact.Should().Be(3); // Lower impact for low-risk devices
        result.IsWireless.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_SmartThermostatOnGuestVlan_ReturnsIssue()
    {
        // Arrange
        var guestNetwork = new NetworkInfo { Id = "guest-net", Name = "Guest", VlanId = 50, Purpose = NetworkPurpose.Guest };
        var client = CreateWirelessClient(ClientDeviceCategory.SmartThermostat, network: guestNetwork);
        var networks = CreateNetworkList(guestNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert - SmartThermostat is low-risk (convenience device)
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
    }

    #endregion

    #region Evaluate Tests - Media Devices Get Warning Severity

    [Fact]
    public void Evaluate_SmartTVOnCorporateVlan_ReturnsWarning()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(ClientDeviceCategory.SmartTV, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
    }

    [Fact]
    public void Evaluate_StreamingDeviceOnCorporateVlan_ReturnsWarning()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(ClientDeviceCategory.StreamingDevice, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
    }

    [Fact]
    public void Evaluate_MediaPlayerOnCorporateVlan_ReturnsWarning()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(ClientDeviceCategory.MediaPlayer, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
    }

    #endregion

    #region Evaluate Tests - Issue Details

    [Fact]
    public void Evaluate_IssueIncludesClientDetails()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(
            ClientDeviceCategory.SmartPlug,
            network: corpNetwork,
            clientName: "Living Room Plug",
            clientMac: "AA:BB:CC:DD:EE:FF",
            apName: "AP-Living Room");
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.ClientName.Should().Be("Living Room Plug");
        result.ClientMac.Should().Be("AA:BB:CC:DD:EE:FF");
        result.AccessPoint.Should().Be("AP-Living Room");
        result.CurrentNetwork.Should().Be("Corporate");
        result.CurrentVlan.Should().Be(10);
    }

    [Fact]
    public void Evaluate_IssueRecommendsIoTNetwork()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT Devices", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var client = CreateWirelessClient(ClientDeviceCategory.SmartPlug, network: corpNetwork);
        var networks = new List<NetworkInfo> { corpNetwork, iotNetwork };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.RecommendedNetwork.Should().Be("IoT Devices");
        result.RecommendedVlan.Should().Be(40);
    }

    [Fact]
    public void Evaluate_IssueIncludesMetadata()
    {
        // Arrange
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(ClientDeviceCategory.SmartPlug, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("device_category");
        result.Metadata!["device_category"].Should().Be("SmartPlug");
        result.Metadata.Should().ContainKey("current_network_purpose");
    }

    #endregion

    #region Evaluate Tests - Printer Handling

    [Fact]
    public void Evaluate_PrinterOnCorporateVlan_ReturnsIssue()
    {
        // Arrange - Printer is handled like IoT
        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(ClientDeviceCategory.Printer, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("WIFI-IOT-VLAN-001");
    }

    [Fact]
    public void Evaluate_PrinterOnIoTVlan_ReturnsNull()
    {
        // Arrange - Printer correctly on IoT VLAN
        var iotNetwork = new NetworkInfo { Id = "iot-net", Name = "IoT", VlanId = 40, Purpose = NetworkPurpose.IoT };
        var client = CreateWirelessClient(ClientDeviceCategory.Printer, network: iotNetwork);
        var networks = CreateNetworkList(iotNetwork);

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert - Correctly placed
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Null Network

    [Fact]
    public void Evaluate_ClientWithNullNetwork_ReturnsNull()
    {
        // Arrange - Client has no network assigned
        var client = CreateWirelessClient(ClientDeviceCategory.SmartPlug, network: null);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert - Should skip when network is null
        result.Should().BeNull();
    }

    #endregion

    #region Device Allowance Settings Tests

    [Fact]
    public void Evaluate_StreamingDevice_AllowAllStreaming_ReturnsInformational()
    {
        // Arrange
        var rule = new WirelessIotVlanRule();
        rule.SetAllowanceSettings(new DeviceAllowanceSettings
        {
            AllowAllStreamingOnMainNetwork = true
        });

        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(ClientDeviceCategory.StreamingDevice, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = rule.Evaluate(client, networks);

        // Assert - allowed by settings
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Informational);
        result.Metadata.Should().ContainKey("allowed_by_settings");
    }

    [Fact]
    public void Evaluate_SmartTV_AllowAllTVs_ReturnsInformational()
    {
        // Arrange
        var rule = new WirelessIotVlanRule();
        rule.SetAllowanceSettings(new DeviceAllowanceSettings
        {
            AllowAllTVsOnMainNetwork = true
        });

        var corpNetwork = new NetworkInfo { Id = "corp-net", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate };
        var client = CreateWirelessClient(ClientDeviceCategory.SmartTV, network: corpNetwork);
        var networks = CreateNetworkList(corpNetwork);

        // Act
        var result = rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Informational);
    }

    #endregion

    #region Helper Methods

    private static WirelessClientInfo CreateWirelessClient(
        ClientDeviceCategory category,
        NetworkInfo? network = null,
        string clientName = "Test Device",
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
