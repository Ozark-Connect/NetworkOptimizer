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
    [InlineData(ClientDeviceCategory.SmartThermostat, true)]  // Thermostats are low-risk (convenience, not security)
    [InlineData(ClientDeviceCategory.SmartLock, false)]       // Locks are high-risk (security/access control)
    [InlineData(ClientDeviceCategory.SmartHub, false)]        // Hubs are high-risk (control many devices)
    [InlineData(ClientDeviceCategory.SmartSensor, true)]      // Sensors are low-risk (temperature, air quality, etc.)
    [InlineData(ClientDeviceCategory.Camera, false)]          // Cameras are high-risk (security)
    [InlineData(ClientDeviceCategory.CloudCamera, false)]     // Cloud cameras are high-risk (security)
    [InlineData(ClientDeviceCategory.IoTGeneric, true)]       // Generic IoT is low-risk
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

    #region Device Allowance Settings Tests

    [Fact]
    public void CheckIoTPlacement_StreamingDevice_AppleWithAllowApple_ReturnsInformational()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.StreamingDevice,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: "Apple Inc");

        result.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(0); // User explicitly allowed - no score penalty
        result.IsLowRisk.Should().BeTrue();
    }

    [Fact]
    public void CheckIoTPlacement_StreamingDevice_RokuWithAllowApple_StaysRecommended()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.StreamingDevice,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: "Roku Inc");

        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(ScoreConstants.LowRiskIoTImpact);
    }

    [Fact]
    public void CheckIoTPlacement_StreamingDevice_AllowAll_ReturnsInformationalForAnyVendor()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings { AllowAllStreamingOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.StreamingDevice,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: "Roku Inc");

        result.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(0); // User explicitly allowed - no score penalty
    }

    [Theory]
    [InlineData("LG Electronics")]
    [InlineData("Samsung")]
    [InlineData("Sony Corporation")]
    public void CheckIoTPlacement_SmartTV_NameBrandWithAllowNameBrand_ReturnsInformational(string vendor)
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings { AllowNameBrandTVsOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartTV,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: vendor);

        result.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(0); // User explicitly allowed - no score penalty
    }

    [Fact]
    public void CheckIoTPlacement_SmartTV_OffBrandWithAllowNameBrand_StaysRecommended()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings { AllowNameBrandTVsOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartTV,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: "TCL Electronics");

        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(ScoreConstants.LowRiskIoTImpact);
    }

    [Fact]
    public void CheckIoTPlacement_SmartTV_AllowAll_ReturnsInformationalForAnyVendor()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings { AllowAllTVsOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartTV,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: "Hisense");

        result.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(0); // User explicitly allowed - no score penalty
    }

    [Fact]
    public void CheckIoTPlacement_HighRiskDevice_IgnoresAllowanceSettings()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings
        {
            AllowAllStreamingOnMainNetwork = true,
            AllowAllTVsOnMainNetwork = true
        };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartLock,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: "August");

        // High-risk devices always get Critical severity regardless of allowance settings
        result.Severity.Should().Be(AuditSeverity.Critical);
        result.ScoreImpact.Should().Be(10);
        result.IsLowRisk.Should().BeFalse();
    }

    [Fact]
    public void CheckIoTPlacement_StreamingDevice_NullVendor_StaysRecommended()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.StreamingDevice,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: null);

        result.Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void CheckIoTPlacement_WithNullAllowanceSettings_UsesDefaultSeverity()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.StreamingDevice,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowanceSettings: null,
            vendorName: "Apple Inc");

        // Without allowance settings, stays at default Recommended for low-risk devices
        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.IsLowRisk.Should().BeTrue();
    }

    [Fact]
    public void CheckIoTPlacement_SmartPlug_IsLowRiskButNotInAllowanceSettings()
    {
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings
        {
            AllowAllStreamingOnMainNetwork = true,
            AllowAllTVsOnMainNetwork = true
        };

        // SmartPlug IS low-risk, but allowance settings only cover StreamingDevice, SmartTV, and SmartSpeaker (Apple only)
        // So SmartPlug stays at Recommended severity, not Informational
        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartPlug,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: "TP-Link");

        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.IsLowRisk.Should().BeTrue();
    }

    [Fact]
    public void CheckIoTPlacement_SmartSpeaker_AppleWithAllowApple_ReturnsInformational()
    {
        // Apple HomePod is categorized as SmartSpeaker
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartSpeaker,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: "Apple Inc");

        result.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(0); // User explicitly allowed - no score penalty
        result.IsLowRisk.Should().BeTrue();
        result.IsAllowedBySettings.Should().BeTrue();
    }

    [Fact]
    public void CheckIoTPlacement_SmartSpeaker_AmazonWithAllowApple_StaysRecommended()
    {
        // Amazon Echo should not be affected by Apple streaming allowance
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartSpeaker,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: "Amazon");

        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(ScoreConstants.LowRiskIoTImpact);
        result.IsAllowedBySettings.Should().BeFalse();
    }

    [Fact]
    public void CheckIoTPlacement_SmartSpeaker_GoogleWithAllowApple_StaysRecommended()
    {
        // Google Home should not be affected by Apple streaming allowance
        var network = new NetworkInfo { Id = "corp", Name = "Corporate", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckIoTPlacement(
            ClientDeviceCategory.SmartSpeaker,
            network,
            TestNetworks,
            defaultScoreImpact: 10,
            allowance,
            vendorName: "Google");

        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.IsAllowedBySettings.Should().BeFalse();
    }

    #endregion

    #region CheckPrinterPlacement Tests

    private static readonly List<NetworkInfo> NetworksWithPrinter = new()
    {
        new NetworkInfo { Id = "1", Name = "Default", VlanId = 1, Purpose = NetworkPurpose.Corporate },
        new NetworkInfo { Id = "2", Name = "IoT", VlanId = 20, Purpose = NetworkPurpose.IoT },
        new NetworkInfo { Id = "3", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security },
        new NetworkInfo { Id = "5", Name = "Printers", VlanId = 50, Purpose = NetworkPurpose.Printer }
    };

    [Fact]
    public void CheckPrinterPlacement_OnPrinterVlan_IsAlwaysCorrect()
    {
        var printerNetwork = new NetworkInfo { Id = "5", Name = "Printers", VlanId = 50, Purpose = NetworkPurpose.Printer };
        var allowance = new DeviceAllowanceSettings { AllowPrintersOnMainNetwork = false };

        var result = VlanPlacementChecker.CheckPrinterPlacement(printerNetwork, NetworksWithPrinter, 10, allowance);

        result.IsCorrectlyPlaced.Should().BeTrue();
        result.IsLowRisk.Should().BeTrue();
    }

    [Fact]
    public void CheckPrinterPlacement_LenientMode_OnIoT_FlagsWithInformationalSeverity()
    {
        var iotNetwork = new NetworkInfo { Id = "2", Name = "IoT", VlanId = 20, Purpose = NetworkPurpose.IoT };
        var allowance = new DeviceAllowanceSettings { AllowPrintersOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckPrinterPlacement(iotNetwork, NetworksWithPrinter, 10, allowance);

        // When Printer VLAN exists, device is flagged but with Informational severity and no score impact
        result.IsCorrectlyPlaced.Should().BeFalse();
        result.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(0);
    }

    [Fact]
    public void CheckPrinterPlacement_LenientMode_OnSecurity_FlagsWithInformationalSeverity()
    {
        var securityNetwork = new NetworkInfo { Id = "3", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var allowance = new DeviceAllowanceSettings { AllowPrintersOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckPrinterPlacement(securityNetwork, NetworksWithPrinter, 10, allowance);

        // When Printer VLAN exists, device is flagged but with Informational severity in lenient mode
        result.IsCorrectlyPlaced.Should().BeFalse();
        result.Severity.Should().Be(AuditSeverity.Informational);
    }

    [Fact]
    public void CheckPrinterPlacement_StrictMode_RequiresPrinterVlan()
    {
        var iotNetwork = new NetworkInfo { Id = "2", Name = "IoT", VlanId = 20, Purpose = NetworkPurpose.IoT };
        var allowance = new DeviceAllowanceSettings { AllowPrintersOnMainNetwork = false };

        var result = VlanPlacementChecker.CheckPrinterPlacement(iotNetwork, NetworksWithPrinter, 10, allowance);

        // Even on IoT, strict mode requires Printer VLAN when one exists
        result.IsCorrectlyPlaced.Should().BeFalse();
        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(10);
        result.RecommendedNetwork!.Purpose.Should().Be(NetworkPurpose.Printer);
    }

    [Fact]
    public void CheckPrinterPlacement_StrictMode_NoPrinterVlan_AcceptsIoT()
    {
        var iotNetwork = new NetworkInfo { Id = "2", Name = "IoT", VlanId = 20, Purpose = NetworkPurpose.IoT };
        var allowance = new DeviceAllowanceSettings { AllowPrintersOnMainNetwork = false };
        // TestNetworks doesn't have a Printer VLAN
        var result = VlanPlacementChecker.CheckPrinterPlacement(iotNetwork, TestNetworks, 10, allowance);

        // Without Printer VLAN, IoT is acceptable even in strict mode
        result.IsCorrectlyPlaced.Should().BeTrue();
        result.RecommendedNetwork!.Purpose.Should().Be(NetworkPurpose.IoT);
    }

    [Fact]
    public void CheckPrinterPlacement_StrictMode_NoPrinterVlan_AcceptsSecurity()
    {
        var securityNetwork = new NetworkInfo { Id = "3", Name = "Security", VlanId = 30, Purpose = NetworkPurpose.Security };
        var allowance = new DeviceAllowanceSettings { AllowPrintersOnMainNetwork = false };

        var result = VlanPlacementChecker.CheckPrinterPlacement(securityNetwork, TestNetworks, 10, allowance);

        result.IsCorrectlyPlaced.Should().BeTrue();
    }

    [Fact]
    public void CheckPrinterPlacement_OnCorporate_IsAlwaysIncorrect()
    {
        var corpNetwork = new NetworkInfo { Id = "1", Name = "Default", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var allowance = new DeviceAllowanceSettings { AllowPrintersOnMainNetwork = true };

        var result = VlanPlacementChecker.CheckPrinterPlacement(corpNetwork, NetworksWithPrinter, 10, allowance);

        // Corporate is never correct, even in lenient mode
        result.IsCorrectlyPlaced.Should().BeFalse();
    }

    [Fact]
    public void CheckPrinterPlacement_DefaultAllowance_UsesInformationalSeverity()
    {
        var iotNetwork = new NetworkInfo { Id = "2", Name = "IoT", VlanId = 20, Purpose = NetworkPurpose.IoT };

        // null allowance defaults to lenient (AllowPrintersOnMainNetwork = true)
        var result = VlanPlacementChecker.CheckPrinterPlacement(iotNetwork, NetworksWithPrinter, 10, null);

        // When Printer VLAN exists, device is flagged but with Informational severity by default
        result.IsCorrectlyPlaced.Should().BeFalse();
        result.Severity.Should().Be(AuditSeverity.Informational);
    }

    [Fact]
    public void CheckPrinterPlacement_RecommendsPrinterVlanOverIoT()
    {
        var corpNetwork = new NetworkInfo { Id = "1", Name = "Default", VlanId = 1, Purpose = NetworkPurpose.Corporate };

        var result = VlanPlacementChecker.CheckPrinterPlacement(corpNetwork, NetworksWithPrinter, 10, null);

        // Should recommend Printer VLAN when available, not IoT
        result.RecommendedNetwork!.Purpose.Should().Be(NetworkPurpose.Printer);
        result.RecommendedNetworkLabel.Should().Be("Printers (50)");
    }

    [Fact]
    public void CheckPrinterPlacement_NoNetworks_ReturnsFallbackLabel()
    {
        var corpNetwork = new NetworkInfo { Id = "1", Name = "Default", VlanId = 1, Purpose = NetworkPurpose.Corporate };
        var emptyNetworks = new List<NetworkInfo>
        {
            new() { Id = "1", Name = "Default", VlanId = 1, Purpose = NetworkPurpose.Corporate }
        };

        var result = VlanPlacementChecker.CheckPrinterPlacement(corpNetwork, emptyNetworks, 10, null);

        result.RecommendedNetwork.Should().BeNull();
        result.RecommendedNetworkLabel.Should().Be("Printer or IoT VLAN");
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
