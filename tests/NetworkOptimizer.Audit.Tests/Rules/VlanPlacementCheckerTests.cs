using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Audit.Scoring;
using NetworkOptimizer.Core.Enums;
using Xunit;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class VlanPlacementCheckerTests
{
    private static readonly List<NetworkInfo> TestNetworks = new()
    {
        new NetworkInfo { Id = "1", Name = "Default", VlanId = 1, Purpose = NetworkPurpose.Corporate },
        new NetworkInfo { Id = "2", Name = "IoT", VlanId = 20, Purpose = NetworkPurpose.IoT },
        new NetworkInfo { Id = "3", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security },
        new NetworkInfo { Id = "4", Name = "IoT2", VlanId = 25, Purpose = NetworkPurpose.IoT }
    };

    #region CheckIoTPlacement Tests

    [Theory]
    [InlineData(NetworkPurpose.IoT, true)]
    [InlineData(NetworkPurpose.Security, true)]
    [InlineData(NetworkPurpose.Corporate, false)]
    [InlineData(NetworkPurpose.Guest, false)]
    public void CheckIoTPlacement_ReturnsCorrectPlacementStatus(NetworkPurpose purpose, bool expectedCorrect)
    {
        var network = new NetworkInfo { Id = "test", Name = "Test", VlanId = 10, Purpose = purpose };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartTV, network, TestNetworks);

        result.IsCorrectlyPlaced.Should().Be(expectedCorrect);
    }

    [Theory]
    [InlineData(ClientDeviceCategory.SmartTV, true)]
    [InlineData(ClientDeviceCategory.SmartSpeaker, true)]
    [InlineData(ClientDeviceCategory.GameConsole, true)]
    [InlineData(ClientDeviceCategory.SmartThermostat, false)]
    [InlineData(ClientDeviceCategory.SmartLock, false)]
    [InlineData(ClientDeviceCategory.SmartHub, false)]
    public void CheckIoTPlacement_DetectsLowRiskDevices(ClientDeviceCategory category, bool expectedLowRisk)
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var result = VlanPlacementChecker.CheckIoTPlacement(category, network, TestNetworks);

        result.IsLowRisk.Should().Be(expectedLowRisk);
    }

    [Fact]
    public void CheckIoTPlacement_RecommendsLowestVlanIoTNetwork()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartTV, network, TestNetworks);

        result.RecommendedNetwork.Should().NotBeNull();
        result.RecommendedNetwork!.Name.Should().Be("IoT");
        result.RecommendedNetwork.VlanId.Should().Be(20);
        result.RecommendedNetworkLabel.Should().Be("IoT (20)");
    }

    [Fact]
    public void CheckIoTPlacement_LowRiskDevice_GetsRecommendedSeverity()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartTV, network, TestNetworks);

        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(ScoreConstants.LowRiskIoTImpact);
    }

    [Fact]
    public void CheckIoTPlacement_HighRiskDevice_GetsCriticalSeverity()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartLock, network, TestNetworks, defaultScoreImpact: 10);

        result.Severity.Should().Be(AuditSeverity.Critical);
        result.ScoreImpact.Should().Be(10);
    }

    [Fact]
    public void CheckIoTPlacement_NoIoTNetwork_ReturnsFallbackLabel()
    {
        var networksWithoutIoT = new List<NetworkInfo>
        {
            new() { Id = "1", Name = "Default", VlanId = 1, Purpose = NetworkPurpose.Corporate }
        };
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartTV, network, networksWithoutIoT);

        result.RecommendedNetwork.Should().BeNull();
        result.RecommendedNetworkLabel.Should().Be("IoT VLAN");
    }

    #endregion

    #region CheckCameraPlacement Tests

    [Theory]
    [InlineData(NetworkPurpose.Security, true)]
    [InlineData(NetworkPurpose.IoT, false)]
    [InlineData(NetworkPurpose.Corporate, false)]
    [InlineData(NetworkPurpose.Guest, false)]
    public void CheckCameraPlacement_ReturnsCorrectPlacementStatus(NetworkPurpose purpose, bool expectedCorrect)
    {
        var network = new NetworkInfo { Id = "test", Name = "Test", VlanId = 10, Purpose = purpose };

        var result = VlanPlacementChecker.CheckCameraPlacement(network, TestNetworks);

        result.IsCorrectlyPlaced.Should().Be(expectedCorrect);
    }

    [Fact]
    public void CheckCameraPlacement_AlwaysReturnsCriticalSeverity()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var result = VlanPlacementChecker.CheckCameraPlacement(network, TestNetworks);

        result.Severity.Should().Be(AuditSeverity.Critical);
        result.IsLowRisk.Should().BeFalse();
    }

    [Fact]
    public void CheckCameraPlacement_RecommendsLowestVlanSecurityNetwork()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var result = VlanPlacementChecker.CheckCameraPlacement(network, TestNetworks);

        result.RecommendedNetwork.Should().NotBeNull();
        result.RecommendedNetwork!.Name.Should().Be("Security");
        result.RecommendedNetwork.VlanId.Should().Be(30);
        result.RecommendedNetworkLabel.Should().Be("Security (30)");
    }

    [Fact]
    public void CheckCameraPlacement_NoSecurityNetwork_ReturnsFallbackLabel()
    {
        var networksWithoutSecurity = new List<NetworkInfo>
        {
            new() { Id = "1", Name = "Default", VlanId = 1, Purpose = NetworkPurpose.Corporate }
        };
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var result = VlanPlacementChecker.CheckCameraPlacement(network, networksWithoutSecurity);

        result.RecommendedNetwork.Should().BeNull();
        result.RecommendedNetworkLabel.Should().Be("Security VLAN");
    }

    [Fact]
    public void CheckCameraPlacement_UsesProvidedScoreImpact()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var result = VlanPlacementChecker.CheckCameraPlacement(network, TestNetworks, defaultScoreImpact: 12);

        result.ScoreImpact.Should().Be(12);
    }

    #endregion

    #region BuildMetadata Tests

    [Fact]
    public void BuildMetadata_IncludesAllRequiredFields()
    {
        var detection = new DeviceDetectionResult
        {
            Category = ClientDeviceCategory.Camera,
            Source = DetectionSource.UniFiFingerprint,
            ConfidenceScore = 95,
            VendorName = "Hikvision"
        };
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var metadata = VlanPlacementChecker.BuildMetadata(detection, network);

        // CategoryName is derived from Category.GetDisplayName()
        metadata["device_type"].Should().Be(detection.CategoryName);
        metadata["device_category"].Should().Be("Camera");
        metadata["detection_source"].Should().Be("UniFiFingerprint");
        metadata["detection_confidence"].Should().Be(95);
        metadata["vendor"].Should().Be("Hikvision");
        metadata["current_network_purpose"].Should().Be("Corporate");
    }

    [Fact]
    public void BuildMetadata_IncludesLowRiskFlag_WhenProvided()
    {
        var detection = new DeviceDetectionResult { Category = ClientDeviceCategory.SmartTV };
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var metadata = VlanPlacementChecker.BuildMetadata(detection, network, isLowRisk: true);

        metadata["is_low_risk_device"].Should().Be(true);
    }

    [Fact]
    public void BuildMetadata_ExcludesLowRiskFlag_WhenNotProvided()
    {
        var detection = new DeviceDetectionResult { Category = ClientDeviceCategory.SmartTV };
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var metadata = VlanPlacementChecker.BuildMetadata(detection, network);

        metadata.ContainsKey("is_low_risk_device").Should().BeFalse();
    }

    [Fact]
    public void BuildMetadata_HandlesNullVendor()
    {
        var detection = new DeviceDetectionResult
        {
            Category = ClientDeviceCategory.SmartTV,
            VendorName = null
        };
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var metadata = VlanPlacementChecker.BuildMetadata(detection, network);

        metadata["vendor"].Should().Be("Unknown");
    }

    [Fact]
    public void BuildMetadata_HandlesNullNetwork()
    {
        var detection = new DeviceDetectionResult { Category = ClientDeviceCategory.SmartTV };

        var metadata = VlanPlacementChecker.BuildMetadata(detection, null);

        metadata["current_network_purpose"].Should().Be("Unknown");
    }

    #endregion
}
