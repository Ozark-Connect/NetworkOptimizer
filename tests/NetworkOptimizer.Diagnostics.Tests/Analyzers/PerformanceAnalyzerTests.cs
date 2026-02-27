using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Diagnostics.Analyzers;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Helpers;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Diagnostics.Tests.Analyzers;

public class PerformanceAnalyzerTests
{
    private readonly DeviceTypeDetectionService _detectionService;
    private readonly PerformanceAnalyzer _analyzer;

    public PerformanceAnalyzerTests()
    {
        _detectionService = new DeviceTypeDetectionService();
        _analyzer = new PerformanceAnalyzer(_detectionService);
    }

    #region Hardware Acceleration

    [Fact]
    public void CheckHardwareAcceleration_NoGateway_ReturnsEmpty()
    {
        var devices = new List<UniFiDeviceResponse>
        {
            CreateSwitch("switch1", "Switch 1")
        };

        var result = _analyzer.CheckHardwareAcceleration(devices);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckHardwareAcceleration_Enabled_ReturnsEmpty()
    {
        var devices = new List<UniFiDeviceResponse>
        {
            CreateGateway(hardwareOffload: true)
        };

        var result = _analyzer.CheckHardwareAcceleration(devices);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckHardwareAcceleration_Null_ReturnsEmpty()
    {
        var devices = new List<UniFiDeviceResponse>
        {
            CreateGateway(hardwareOffload: null)
        };

        var result = _analyzer.CheckHardwareAcceleration(devices);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckHardwareAcceleration_Disabled_ReturnsIssue()
    {
        var devices = new List<UniFiDeviceResponse>
        {
            CreateGateway(hardwareOffload: false)
        };

        var result = _analyzer.CheckHardwareAcceleration(devices);

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Hardware Acceleration Disabled");
        result[0].Severity.Should().Be(PerformanceSeverity.Info);
        result[0].Category.Should().Be(PerformanceCategory.Performance);
        result[0].DeviceName.Should().Be("Test Gateway");
    }

    [Fact]
    public void CheckHardwareAcceleration_Disabled_NetFlowEnabled_Suppressed()
    {
        var devices = new List<UniFiDeviceResponse>
        {
            CreateGateway(hardwareOffload: false)
        };
        var settings = CreateSettingsWithNetFlow(netflowEnabled: true);

        var result = _analyzer.CheckHardwareAcceleration(devices, settings);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckHardwareAcceleration_Disabled_NetFlowDisabled_ReturnsIssue()
    {
        var devices = new List<UniFiDeviceResponse>
        {
            CreateGateway(hardwareOffload: false)
        };
        var settings = CreateSettingsWithNetFlow(netflowEnabled: false);

        var result = _analyzer.CheckHardwareAcceleration(devices, settings);

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Hardware Acceleration Disabled");
    }

    [Fact]
    public void IsNetFlowEnabled_Enabled_ReturnsTrue()
    {
        var settings = CreateSettingsWithNetFlow(netflowEnabled: true);
        PerformanceAnalyzer.IsNetFlowEnabled(settings).Should().BeTrue();
    }

    [Fact]
    public void IsNetFlowEnabled_Disabled_ReturnsFalse()
    {
        var settings = CreateSettingsWithNetFlow(netflowEnabled: false);
        PerformanceAnalyzer.IsNetFlowEnabled(settings).Should().BeFalse();
    }

    [Fact]
    public void IsNetFlowEnabled_NoSettings_ReturnsFalse()
    {
        PerformanceAnalyzer.IsNetFlowEnabled(null).Should().BeFalse();
    }

    [Fact]
    public void IsNetFlowEnabled_NoNetFlowKey_ReturnsFalse()
    {
        var settings = CreateSettings(); // has global_switch but no netflow
        PerformanceAnalyzer.IsNetFlowEnabled(settings).Should().BeFalse();
    }

    #endregion

    #region Jumbo Frames

    [Fact]
    public void CheckJumboFrames_AlreadyEnabled_ReturnsEmpty()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(1000, 2500, 2500) };
        var settings = CreateSettings(jumboEnabled: true);

        var result = _analyzer.CheckJumboFrames(devices, settings);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckJumboFrames_NoHighSpeedPorts_ReturnsEmpty()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(1000, 1000, 1000) };
        var settings = CreateSettings(jumboEnabled: false);

        var result = _analyzer.CheckJumboFrames(devices, settings);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckJumboFrames_OneHighSpeedPort_ReturnsEmpty()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(1000, 2500) };
        var settings = CreateSettings(jumboEnabled: false);

        var result = _analyzer.CheckJumboFrames(devices, settings);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckJumboFrames_TwoHighSpeedPorts_ReturnsIssue()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(1000, 2500, 2500) };
        var settings = CreateSettings(jumboEnabled: false);

        var result = _analyzer.CheckJumboFrames(devices, settings);

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Jumbo Frames Not Enabled");
        result[0].Severity.Should().Be(PerformanceSeverity.Info);
        result[0].Category.Should().Be(PerformanceCategory.Performance);
    }

    [Fact]
    public void CheckJumboFrames_TenGigPorts_ReturnsIssue()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(10000, 10000) };
        var settings = CreateSettings(jumboEnabled: false);

        var result = _analyzer.CheckJumboFrames(devices, settings);

        result.Should().HaveCount(1);
        result[0].Description.Should().Contain("2 access ports");
    }

    [Fact]
    public void CheckJumboFrames_NullSettings_ReturnsIssueIfHighSpeedPorts()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(2500, 2500) };

        var result = _analyzer.CheckJumboFrames(devices, null);

        result.Should().HaveCount(1);
    }

    [Fact]
    public void CheckJumboFrames_UplinkPortsExcluded()
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 10000, Up = true, IsUplink = true },
            new() { PortIdx = 2, Speed = 10000, Up = true, IsUplink = true },
            new() { PortIdx = 3, Speed = 1000, Up = true, IsUplink = false }
        };
        var settings = CreateSettings(jumboEnabled: false);

        var result = _analyzer.CheckJumboFrames(new List<UniFiDeviceResponse> { device }, settings);

        result.Should().BeEmpty();
    }

    #endregion

    #region Flow Control

    [Fact]
    public void CheckFlowControl_AlreadyEnabled_ReturnsEmpty()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(1000, 2500) };
        var networks = CreateWanNetwork(1000);
        var settings = CreateSettings(flowCtrlEnabled: true);

        var result = _analyzer.CheckFlowControl(devices, networks, new List<UniFiClientResponse>(), settings);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckFlowControl_FastWan_ReturnsIssue()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(1000) };
        var networks = CreateWanNetwork(1000);
        var settings = CreateSettings(flowCtrlEnabled: false);

        var result = _analyzer.CheckFlowControl(devices, networks, new List<UniFiClientResponse>(), settings);

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Consider Flow Control");
        result[0].Description.Should().Contain("800 Mbps");
        result[0].Severity.Should().Be(PerformanceSeverity.Info);
        result[0].Category.Should().Be(PerformanceCategory.Performance);
    }

    [Fact]
    public void CheckFlowControl_SlowWan_NoMixedSpeeds_ReturnsEmpty()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(1000, 1000) };
        var networks = CreateWanNetwork(500);
        var settings = CreateSettings(flowCtrlEnabled: false);

        var result = _analyzer.CheckFlowControl(devices, networks, new List<UniFiClientResponse>(), settings);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckFlowControl_MixedSpeedsWithManyWifiDevices_ReturnsIssue()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(1000, 2500) };
        var networks = CreateWanNetwork(500);
        var settings = CreateSettings(flowCtrlEnabled: false);
        var clients = CreateWirelessClients(15);

        var result = _analyzer.CheckFlowControl(devices, networks, clients, settings);

        result.Should().HaveCount(1);
        result[0].Description.Should().Contain("mixed port speeds");
        result[0].Description.Should().Contain("15");
    }

    [Fact]
    public void CheckFlowControl_MixedSpeedsFewWifiDevices_ReturnsEmpty()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(1000, 2500) };
        var networks = CreateWanNetwork(500);
        var settings = CreateSettings(flowCtrlEnabled: false);
        var clients = CreateWirelessClients(5);

        var result = _analyzer.CheckFlowControl(devices, networks, clients, settings);

        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckFlowControl_BothConditions_DescriptionMentionsBoth()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitchWithPorts(1000, 2500) };
        var networks = CreateWanNetwork(1000);
        var settings = CreateSettings(flowCtrlEnabled: false);
        var clients = CreateWirelessClients(15);

        var result = _analyzer.CheckFlowControl(devices, networks, clients, settings);

        result.Should().HaveCount(1);
        result[0].Description.Should().Contain("fast WAN");
        result[0].Description.Should().Contain("mixed-speed");
    }

    [Fact]
    public void CheckFlowControl_WanPortsExcludedFromSpeedTiers()
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 1000, Up = true, IsUplink = false, NetworkName = "WAN" },
            new() { PortIdx = 2, Speed = 2500, Up = true, IsUplink = false, NetworkName = "LAN" },
            new() { PortIdx = 3, Speed = 2500, Up = true, IsUplink = false, NetworkName = "LAN" }
        };
        var networks = CreateWanNetwork(500);
        var settings = CreateSettings(flowCtrlEnabled: false);

        var result = _analyzer.CheckFlowControl(
            new List<UniFiDeviceResponse> { device }, networks, new List<UniFiClientResponse>(), settings);

        // Only one speed tier (2500) since WAN port excluded - no mixed speeds
        result.Should().BeEmpty();
    }

    #endregion

    #region Cellular QoS

    [Fact]
    public void CheckCellularQos_NoGateway_ReturnsEmpty()
    {
        var devices = new List<UniFiDeviceResponse> { CreateSwitch("switch1", "Switch 1") };

        var result = _analyzer.CheckCellularQos(devices, null, null);

        result.Should().BeEmpty();
        _analyzer.CellularWanDetected.Should().BeFalse();
    }

    [Fact]
    public void CheckCellularQos_NoCellularWan_ReturnsEmpty()
    {
        var gateway = CreateGatewayWithWan("wan1", "ethernet");

        var result = _analyzer.CheckCellularQos(
            new List<UniFiDeviceResponse> { gateway }, null, null);

        result.Should().BeEmpty();
        _analyzer.CellularWanDetected.Should().BeFalse();
    }

    [Fact]
    public void CheckCellularQos_CellularWan_NoQosRules_ReturnsAllCategories()
    {
        var gateway = CreateGatewayWithWan("wan3", "wireless_5g");

        var result = _analyzer.CheckCellularQos(
            new List<UniFiDeviceResponse> { gateway }, null, null);

        result.Should().HaveCount(3);
        result.Select(i => i.Title).Should().Contain("Streaming Video Not Rate-Limited");
        result.Select(i => i.Title).Should().Contain("Cloud Sync Not Rate-Limited");
        result.Select(i => i.Title).Should().Contain("Game/App Downloads Not Rate-Limited");
        _analyzer.CellularWanDetected.Should().BeTrue();
    }

    [Fact]
    public void CheckCellularQos_CellularWan_AllCategoriesCovered_ReturnsEmpty()
    {
        var gateway = CreateGatewayWithWan("wan3", "wireless_5g");
        var enriched = CreateEnrichedConfig("wan3", "WAN3", "failover-only", "cellular-wan-id-123");
        var qosRules = CreateQosRulesForAllCategories("cellular-wan-id-123");

        var result = _analyzer.CheckCellularQos(
            new List<UniFiDeviceResponse> { gateway }, qosRules, enriched);

        result.Should().BeEmpty();
        _analyzer.CellularWanDetected.Should().BeTrue();
    }

    [Fact]
    public void CheckCellularQos_FailoverMode_RecommendationSeverity()
    {
        var gateway = CreateGatewayWithWan("wan3", "wireless_5g");
        var enriched = CreateEnrichedConfig("wan3", "WAN3", "failover-only", "cellular-id");

        var result = _analyzer.CheckCellularQos(
            new List<UniFiDeviceResponse> { gateway }, null, enriched);

        result.Should().AllSatisfy(i =>
            i.Severity.Should().Be(PerformanceSeverity.Recommendation));
    }

    [Fact]
    public void CheckCellularQos_LoadBalanced_InfoSeverity()
    {
        var gateway = CreateGatewayWithWan("wan3", "wireless_5g");
        var enriched = CreateEnrichedConfig("wan3", "WAN3", "weighted", "cellular-id");

        var result = _analyzer.CheckCellularQos(
            new List<UniFiDeviceResponse> { gateway }, null, enriched);

        result.Should().AllSatisfy(i =>
            i.Severity.Should().Be(PerformanceSeverity.Info));
    }

    [Fact]
    public void CheckCellularQos_SmallDataPlan_RecommendationSeverity()
    {
        var gateway = CreateGatewayWithWan("wan3", "wireless_5g");
        var enriched = CreateEnrichedConfig("wan3", "WAN3", "weighted", "cellular-id");
        var modem = CreateModem(dataLimitEnabled: true, dataLimitBytes: 100L * 1024 * 1024 * 1024); // 100 GB

        var result = _analyzer.CheckCellularQos(
            new List<UniFiDeviceResponse> { gateway, modem }, null, enriched);

        result.Should().AllSatisfy(i =>
            i.Severity.Should().Be(PerformanceSeverity.Recommendation));
    }

    [Fact]
    public void CheckCellularQos_LargeDataPlan_LoadBalanced_InfoSeverity()
    {
        var gateway = CreateGatewayWithWan("wan3", "wireless_5g");
        var enriched = CreateEnrichedConfig("wan3", "WAN3", "weighted", "cellular-id");
        var modem = CreateModem(dataLimitEnabled: true, dataLimitBytes: 1000L * 1024 * 1024 * 1024); // 1 TB

        var result = _analyzer.CheckCellularQos(
            new List<UniFiDeviceResponse> { gateway, modem }, null, enriched);

        result.Should().AllSatisfy(i =>
            i.Severity.Should().Be(PerformanceSeverity.Info));
    }

    [Fact]
    public void CheckCellularQos_AllIssuesAreCellularCategory()
    {
        var gateway = CreateGatewayWithWan("wan3", "lte");

        var result = _analyzer.CheckCellularQos(
            new List<UniFiDeviceResponse> { gateway }, null, null);

        result.Should().AllSatisfy(i =>
            i.Category.Should().Be(PerformanceCategory.CellularDataSavings));
    }

    [Fact]
    public void CheckCellularQos_LteWanType_Detected()
    {
        var gateway = CreateGatewayWithWan("wan2", "lte");

        var result = _analyzer.CheckCellularQos(
            new List<UniFiDeviceResponse> { gateway }, null, null);

        _analyzer.CellularWanDetected.Should().BeTrue();
        result.Should().HaveCount(3);
    }

    [Fact]
    public void CheckCellularQos_WirelessLteWanType_Detected()
    {
        var gateway = CreateGatewayWithWan("wan2", "wireless_lte");

        var result = _analyzer.CheckCellularQos(
            new List<UniFiDeviceResponse> { gateway }, null, null);

        _analyzer.CellularWanDetected.Should().BeTrue();
    }

    [Fact]
    public void CheckCellularQos_RecommendationsContainHowToLink()
    {
        var gateway = CreateGatewayWithWan("wan3", "wireless_5g");

        var result = _analyzer.CheckCellularQos(
            new List<UniFiDeviceResponse> { gateway }, null, null);

        result.Should().AllSatisfy(i =>
            i.Recommendation.Should().Contain("ozarkconnect.net/blog/unifi-5g-backup-qos"));
    }

    #endregion

    #region QoS Rule Filtering

    [Fact]
    public void GetTargetedAppIds_NullData_ReturnsEmpty()
    {
        var result = PerformanceAnalyzer.GetTargetedAppIds(null, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetTargetedAppIds_DisabledRules_Skipped()
    {
        var rules = CreateQosRulesDoc(new[]
        {
            CreateQosRuleJson("Rule 1", "LIMIT", false, new[] { 262256 }, "wan-id")
        });

        var result = PerformanceAnalyzer.GetTargetedAppIds(rules, "wan-id");

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetTargetedAppIds_PrioritizeRules_Skipped()
    {
        var rules = CreateQosRulesDoc(new[]
        {
            CreateQosRuleJson("Rule 1", "PRIORITIZE", true, new[] { 262256 }, "wan-id")
        });

        var result = PerformanceAnalyzer.GetTargetedAppIds(rules, "wan-id");

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetTargetedAppIds_WrongWan_Skipped()
    {
        var rules = CreateQosRulesDoc(new[]
        {
            CreateQosRuleJson("Rule 1", "LIMIT", true, new[] { 262256, 262276 }, "other-wan-id")
        });

        var result = PerformanceAnalyzer.GetTargetedAppIds(rules, "cellular-wan-id");

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetTargetedAppIds_NoWanField_Skipped()
    {
        var rules = CreateQosRulesDoc(new[]
        {
            CreateQosRuleJson("Rule 1", "LIMIT", true, new[] { 262256, 262276 }, null)
        });

        var result = PerformanceAnalyzer.GetTargetedAppIds(rules, "cellular-wan-id");

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetTargetedAppIds_MatchingWan_Collected()
    {
        var rules = CreateQosRulesDoc(new[]
        {
            CreateQosRuleJson("Rule 1", "LIMIT", true, new[] { 262256, 262276 }, "cellular-wan-id")
        });

        var result = PerformanceAnalyzer.GetTargetedAppIds(rules, "cellular-wan-id");

        result.Should().HaveCount(2);
        result.Should().Contain(262256);
        result.Should().Contain(262276);
    }

    [Fact]
    public void GetTargetedAppIds_MultipleRules_Aggregated()
    {
        var rules = CreateQosRulesDoc(new[]
        {
            CreateQosRuleJson("Streaming", "LIMIT", true, new[] { 262256, 262276 }, "wan-id"),
            CreateQosRuleJson("Cloud", "LIMIT", true, new[] { 196623, 196629 }, "wan-id")
        });

        var result = PerformanceAnalyzer.GetTargetedAppIds(rules, "wan-id");

        result.Should().HaveCount(4);
    }

    [Fact]
    public void GetTargetedAppIds_NullCellularWanId_AcceptsAllRules()
    {
        var rules = CreateQosRulesDoc(new[]
        {
            CreateQosRuleJson("Rule 1", "LIMIT", true, new[] { 262256 }, null),
            CreateQosRuleJson("Rule 2", "LIMIT", true, new[] { 262276 }, "some-wan")
        });

        var result = PerformanceAnalyzer.GetTargetedAppIds(rules, null);

        result.Should().HaveCount(2);
    }

    #endregion

    #region IsCellularFailover

    [Fact]
    public void IsCellularFailover_NullEnrichedData_ReturnsTrue()
    {
        var wan = new GatewayWanInterface { Key = "wan3", Type = "wireless_5g" };

        var result = PerformanceAnalyzer.IsCellularFailover(wan, null);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsCellularFailover_FailoverOnly_ReturnsTrue()
    {
        var wan = new GatewayWanInterface { Key = "wan3", Type = "wireless_5g" };
        var enriched = CreateEnrichedConfig("wan3", "WAN3", "failover-only", "id-123");

        var result = PerformanceAnalyzer.IsCellularFailover(wan, enriched);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsCellularFailover_Weighted_ReturnsFalse()
    {
        var wan = new GatewayWanInterface { Key = "wan3", Type = "wireless_5g" };
        var enriched = CreateEnrichedConfig("wan3", "WAN3", "weighted", "id-123");

        var result = PerformanceAnalyzer.IsCellularFailover(wan, enriched);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsCellularFailover_CaseInsensitiveMatch()
    {
        var wan = new GatewayWanInterface { Key = "wan3", Type = "wireless_5g" };
        var enriched = CreateEnrichedConfig("wan3", "WAN3", "FAILOVER-ONLY", "id-123");

        var result = PerformanceAnalyzer.IsCellularFailover(wan, enriched);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsCellularFailover_NoMatchingNetworkGroup_ReturnsTrue()
    {
        var wan = new GatewayWanInterface { Key = "wan3", Type = "wireless_5g" };
        var enriched = CreateEnrichedConfig("wan1", "WAN", "weighted", "id-123");

        var result = PerformanceAnalyzer.IsCellularFailover(wan, enriched);

        result.Should().BeTrue();
    }

    #endregion

    #region GetModemDataLimit

    [Fact]
    public void GetModemDataLimit_NullModem_ReturnsDisabled()
    {
        var result = PerformanceAnalyzer.GetModemDataLimit(null);

        result.Enabled.Should().BeFalse();
        result.Bytes.Should().Be(0);
    }

    [Fact]
    public void GetModemDataLimit_NoAdditionalData_ReturnsDisabled()
    {
        var modem = new UniFiDeviceResponse { Type = "umbb" };

        var result = PerformanceAnalyzer.GetModemDataLimit(modem);

        result.Enabled.Should().BeFalse();
    }

    [Fact]
    public void GetModemDataLimit_DataLimitEnabled_ReturnsLimit()
    {
        var modem = CreateModem(dataLimitEnabled: true, dataLimitBytes: 200L * 1024 * 1024 * 1024);

        var result = PerformanceAnalyzer.GetModemDataLimit(modem);

        result.Enabled.Should().BeTrue();
        result.Bytes.Should().Be(200L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void GetModemDataLimit_DataLimitDisabled_ReturnsDisabled()
    {
        var modem = CreateModem(dataLimitEnabled: false, dataLimitBytes: 200L * 1024 * 1024 * 1024);

        var result = PerformanceAnalyzer.GetModemDataLimit(modem);

        result.Enabled.Should().BeFalse();
    }

    #endregion

    #region GetCellularWanConfigId

    [Fact]
    public void GetCellularWanConfigId_NullEnrichedData_ReturnsNull()
    {
        var wan = new GatewayWanInterface { Key = "wan3" };

        var result = PerformanceAnalyzer.GetCellularWanConfigId(wan, null);

        result.Should().BeNull();
    }

    [Fact]
    public void GetCellularWanConfigId_MatchingNetworkGroup_ReturnsId()
    {
        var wan = new GatewayWanInterface { Key = "wan3" };
        var enriched = CreateEnrichedConfig("wan3", "WAN3", "failover-only", "abc-123");

        var result = PerformanceAnalyzer.GetCellularWanConfigId(wan, enriched);

        result.Should().Be("abc-123");
    }

    [Fact]
    public void GetCellularWanConfigId_NoMatch_ReturnsNull()
    {
        var wan = new GatewayWanInterface { Key = "wan3" };
        var enriched = CreateEnrichedConfig("wan1", "WAN", "weighted", "other-id");

        var result = PerformanceAnalyzer.GetCellularWanConfigId(wan, enriched);

        result.Should().BeNull();
    }

    #endregion

    #region GlobalSwitchSettings

    [Fact]
    public void GlobalSwitchSettings_NullSettings_ReturnsNull()
    {
        var result = GlobalSwitchSettings.FromSettingsJson(null);

        result.Should().BeNull();
    }

    [Fact]
    public void GlobalSwitchSettings_ParsesJumboEnabled()
    {
        var settings = CreateSettings(jumboEnabled: true);

        var result = GlobalSwitchSettings.FromSettingsJson(settings);

        result.Should().NotBeNull();
        result!.JumboFramesEnabled.Should().BeTrue();
    }

    [Fact]
    public void GlobalSwitchSettings_ParsesFlowControlEnabled()
    {
        var settings = CreateSettings(flowCtrlEnabled: true);

        var result = GlobalSwitchSettings.FromSettingsJson(settings);

        result.Should().NotBeNull();
        result!.FlowControlEnabled.Should().BeTrue();
    }

    [Fact]
    public void GlobalSwitchSettings_ParsesExclusions()
    {
        var settings = CreateSettings(jumboEnabled: true, exclusions: new[] { "aa:bb:cc:00:00:01" });

        var result = GlobalSwitchSettings.FromSettingsJson(settings);

        result.Should().NotBeNull();
        result!.IsExcluded("aa:bb:cc:00:00:01").Should().BeTrue();
        result!.IsExcluded("aa:bb:cc:00:00:02").Should().BeFalse();
    }

    [Fact]
    public void GlobalSwitchSettings_ExclusionsCaseInsensitive()
    {
        var settings = CreateSettings(jumboEnabled: true, exclusions: new[] { "aa:bb:cc:00:00:01" });

        var result = GlobalSwitchSettings.FromSettingsJson(settings);

        result!.IsExcluded("AA:BB:CC:00:00:01").Should().BeTrue();
    }

    [Fact]
    public void GlobalSwitchSettings_GetEffectiveJumboFrames_NonExcludedUsesGlobal()
    {
        var settings = CreateSettings(jumboEnabled: true, exclusions: new[] { "aa:bb:cc:00:00:99" });
        var gss = GlobalSwitchSettings.FromSettingsJson(settings)!;
        var device = new UniFiDeviceResponse { Mac = "aa:bb:cc:00:00:01", JumboFrameEnabled = false };

        gss.GetEffectiveJumboFrames(device).Should().BeTrue(); // uses global
    }

    [Fact]
    public void GlobalSwitchSettings_GetEffectiveJumboFrames_ExcludedUsesDeviceLevel()
    {
        var settings = CreateSettings(jumboEnabled: true, exclusions: new[] { "aa:bb:cc:00:00:01" });
        var gss = GlobalSwitchSettings.FromSettingsJson(settings)!;
        var device = new UniFiDeviceResponse { Mac = "aa:bb:cc:00:00:01", JumboFrameEnabled = false };

        gss.GetEffectiveJumboFrames(device).Should().BeFalse(); // uses device
    }

    [Fact]
    public void GlobalSwitchSettings_GetEffectiveFlowControl_ExcludedUsesDeviceLevel()
    {
        var settings = CreateSettings(flowCtrlEnabled: false, exclusions: new[] { "aa:bb:cc:00:00:01" });
        var gss = GlobalSwitchSettings.FromSettingsJson(settings)!;
        var device = new UniFiDeviceResponse { Mac = "aa:bb:cc:00:00:01", FlowControlEnabled = true };

        gss.GetEffectiveFlowControl(device).Should().BeTrue(); // uses device
    }

    #endregion

    #region Jumbo Frames - Exclusion Scenarios

    [Fact]
    public void CheckJumboFrames_GlobalOn_ExcludedDeviceOff_ReturnsMismatchIssue()
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.Mac = "aa:bb:cc:00:00:01";
        device.JumboFrameEnabled = false;
        device.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 2500, Up = true, IsUplink = false }
        };
        var settings = CreateSettings(jumboEnabled: true, exclusions: new[] { "aa:bb:cc:00:00:01" });

        var result = _analyzer.CheckJumboFrames(new List<UniFiDeviceResponse> { device }, settings);

        result.Should().HaveCount(1);
        result[0].Title.Should().Contain("Switch 1");
        result[0].Severity.Should().Be(PerformanceSeverity.Recommendation);
    }

    [Fact]
    public void CheckJumboFrames_GlobalOn_ExcludedDeviceOn_SuggestsAbsorbingGlobal()
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.Mac = "aa:bb:cc:00:00:01";
        device.JumboFrameEnabled = true;
        device.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 2500, Up = true, IsUplink = false }
        };
        var settings = CreateSettings(jumboEnabled: true, exclusions: new[] { "aa:bb:cc:00:00:01" });

        var result = _analyzer.CheckJumboFrames(new List<UniFiDeviceResponse> { device }, settings);

        result.Should().HaveCount(1);
        result[0].Title.Should().Contain("Per-Device");
        result[0].Title.Should().Contain("Switch 1");
        result[0].Severity.Should().Be(PerformanceSeverity.Info);
        result[0].Description.Should().Contain("Global Switch Settings");
    }

    [Fact]
    public void CheckJumboFrames_GlobalOff_AllExcludedOn_ReturnsPerDeviceIssue()
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.Mac = "aa:bb:cc:00:00:01";
        device.JumboFrameEnabled = true;
        device.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 2500, Up = true, IsUplink = false }
        };
        var settings = CreateSettings(jumboEnabled: false, exclusions: new[] { "aa:bb:cc:00:00:01" });

        var result = _analyzer.CheckJumboFrames(new List<UniFiDeviceResponse> { device }, settings);

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Jumbo Frames Set Per-Device");
        result[0].Severity.Should().Be(PerformanceSeverity.Info);
    }

    [Fact]
    public void CheckJumboFrames_GlobalOff_SomeExcludedOn_ReturnsMismatchWithHighSpeedPorts()
    {
        var switch1 = CreateSwitch("switch1", "Switch A");
        switch1.Mac = "aa:bb:cc:00:00:01";
        switch1.JumboFrameEnabled = true;
        switch1.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 2500, Up = true, IsUplink = false }
        };
        var switch2 = CreateSwitch("switch2", "Switch B");
        switch2.Mac = "aa:bb:cc:00:00:02";
        switch2.JumboFrameEnabled = false;
        switch2.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 2500, Up = true, IsUplink = false }
        };
        var settings = CreateSettings(jumboEnabled: false, exclusions: new[] { "aa:bb:cc:00:00:01", "aa:bb:cc:00:00:02" });

        var result = _analyzer.CheckJumboFrames(new List<UniFiDeviceResponse> { switch1, switch2 }, settings);

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Jumbo Frames Not Enabled");
        result[0].Severity.Should().Be(PerformanceSeverity.Recommendation);
        result[0].Description.Should().Contain("Switch A");
    }

    #endregion

    #region Flow Control - Exclusion Scenarios

    [Fact]
    public void CheckFlowControl_GlobalOn_ExcludedDeviceOff_ReturnsMismatchIssue()
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.Mac = "aa:bb:cc:00:00:01";
        device.FlowControlEnabled = false;
        device.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 1000, Up = true, IsUplink = false }
        };
        var settings = CreateSettings(flowCtrlEnabled: true, exclusions: new[] { "aa:bb:cc:00:00:01" });

        var result = _analyzer.CheckFlowControl(
            new List<UniFiDeviceResponse> { device }, CreateWanNetwork(1000),
            new List<UniFiClientResponse>(), settings);

        result.Should().HaveCount(1);
        result[0].Title.Should().Contain("Switch 1");
        result[0].Severity.Should().Be(PerformanceSeverity.Recommendation);
    }

    [Fact]
    public void CheckFlowControl_GlobalOff_AllExcludedOn_ReturnsPerDeviceIssue()
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.Mac = "aa:bb:cc:00:00:01";
        device.FlowControlEnabled = true;
        device.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 1000, Up = true, IsUplink = false }
        };
        var settings = CreateSettings(flowCtrlEnabled: false, exclusions: new[] { "aa:bb:cc:00:00:01" });

        var result = _analyzer.CheckFlowControl(
            new List<UniFiDeviceResponse> { device }, CreateWanNetwork(1000),
            new List<UniFiClientResponse>(), settings);

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Flow Control Set Per-Device");
        result[0].Severity.Should().Be(PerformanceSeverity.Info);
    }

    [Fact]
    public void CheckFlowControl_GlobalOn_ExcludedDeviceOn_SuggestsAbsorbingGlobal()
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.Mac = "aa:bb:cc:00:00:01";
        device.FlowControlEnabled = true;
        device.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 1000, Up = true, IsUplink = false }
        };
        var settings = CreateSettings(flowCtrlEnabled: true, exclusions: new[] { "aa:bb:cc:00:00:01" });

        var result = _analyzer.CheckFlowControl(
            new List<UniFiDeviceResponse> { device }, CreateWanNetwork(1000),
            new List<UniFiClientResponse>(), settings);

        result.Should().HaveCount(1);
        result[0].Title.Should().Contain("Per-Device");
        result[0].Title.Should().Contain("Switch 1");
        result[0].Severity.Should().Be(PerformanceSeverity.Info);
        result[0].Description.Should().Contain("Global Switch Settings");
    }

    [Fact]
    public void CheckFlowControl_GlobalOn_ExcludedGateway_IgnoresGateway()
    {
        var gateway = CreateGateway();
        gateway.Mac = "aa:bb:cc:00:00:01";
        gateway.FlowControlEnabled = false;
        var settings = CreateSettings(flowCtrlEnabled: true, exclusions: new[] { "aa:bb:cc:00:00:01" });

        var result = _analyzer.CheckFlowControl(
            new List<UniFiDeviceResponse> { gateway }, CreateWanNetwork(1000),
            new List<UniFiClientResponse>(), settings);

        // Gateway should not get a flow control mismatch issue
        result.Should().BeEmpty();
    }

    [Fact]
    public void CheckFlowControl_GlobalOff_ExcludedGatewayOn_IgnoresGateway()
    {
        var gateway = CreateGateway();
        gateway.Mac = "aa:bb:cc:00:00:01";
        gateway.FlowControlEnabled = true;
        // Use slow WAN so the general "Flow Control Not Enabled" check doesn't trigger
        var settings = CreateSettings(flowCtrlEnabled: false, exclusions: new[] { "aa:bb:cc:00:00:01" });

        var result = _analyzer.CheckFlowControl(
            new List<UniFiDeviceResponse> { gateway }, CreateWanNetwork(500),
            new List<UniFiClientResponse>(), settings);

        // Gateway should not trigger "Flow Control Set Per-Device" issue
        result.Should().BeEmpty();
    }

    #endregion

    #region CountHighSpeedAccessPorts

    [Fact]
    public void CountHighSpeedAccessPorts_NoPorts_ReturnsZero()
    {
        var devices = new List<UniFiDeviceResponse>
        {
            new() { Type = "usw", PortTable = null }
        };

        var result = PerformanceAnalyzer.CountHighSpeedAccessPorts(devices);

        result.Should().Be(0);
    }

    [Fact]
    public void CountHighSpeedAccessPorts_ExcludesUplinks()
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 10000, Up = true, IsUplink = true },
            new() { PortIdx = 2, Speed = 2500, Up = true, IsUplink = false }
        };

        var result = PerformanceAnalyzer.CountHighSpeedAccessPorts(new List<UniFiDeviceResponse> { device });

        result.Should().Be(1);
    }

    [Fact]
    public void CountHighSpeedAccessPorts_ExcludesDownPorts()
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 2500, Up = false, IsUplink = false },
            new() { PortIdx = 2, Speed = 2500, Up = true, IsUplink = false }
        };

        var result = PerformanceAnalyzer.CountHighSpeedAccessPorts(new List<UniFiDeviceResponse> { device });

        result.Should().Be(1);
    }

    [Fact]
    public void CountHighSpeedAccessPorts_ExcludesWanPorts()
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.PortTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 2500, Up = true, IsUplink = false, NetworkName = "WAN" },
            new() { PortIdx = 2, Speed = 2500, Up = true, IsUplink = false, NetworkName = "LAN" }
        };

        var result = PerformanceAnalyzer.CountHighSpeedAccessPorts(new List<UniFiDeviceResponse> { device });

        result.Should().Be(1);
    }

    [Fact]
    public void CountHighSpeedAccessPorts_AcrossMultipleSwitches()
    {
        var switch1 = CreateSwitchWithPorts(2500, 2500);
        var switch2 = CreateSwitchWithPorts(2500, 1000);

        var result = PerformanceAnalyzer.CountHighSpeedAccessPorts(
            new List<UniFiDeviceResponse> { switch1, switch2 });

        result.Should().Be(3);
    }

    #endregion

    #region Analyze Integration

    [Fact]
    public void Analyze_PerformanceChecksDisabled_SkipsPerformance()
    {
        var devices = new List<UniFiDeviceResponse> { CreateGateway(hardwareOffload: false) };

        var result = _analyzer.Analyze(
            devices, new(), new(), null, null,
            runPerformanceChecks: false, runCellularChecks: false);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_CellularChecksDisabled_SkipsCellular()
    {
        var gateway = CreateGatewayWithWan("wan3", "wireless_5g");

        var result = _analyzer.Analyze(
            new List<UniFiDeviceResponse> { gateway }, new(), new(), null, null,
            runPerformanceChecks: false, runCellularChecks: false);

        result.Should().BeEmpty();
        _analyzer.CellularWanDetected.Should().BeFalse();
    }

    [Fact]
    public void Analyze_BothEnabled_RunsBoth()
    {
        var gateway = CreateGatewayWithWan("wan3", "wireless_5g");
        gateway.HardwareOffload = false;

        var result = _analyzer.Analyze(
            new List<UniFiDeviceResponse> { gateway }, new(), new(), null, null,
            runPerformanceChecks: true, runCellularChecks: true);

        result.Should().Contain(i => i.Category == PerformanceCategory.Performance);
        result.Should().Contain(i => i.Category == PerformanceCategory.CellularDataSavings);
    }

    #endregion

    #region Helper Methods

    private static UniFiDeviceResponse CreateGateway(bool? hardwareOffload = null)
    {
        return new UniFiDeviceResponse
        {
            Id = "gateway1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Test Gateway",
            Type = "ugw",
            HardwareOffload = hardwareOffload
        };
    }

    private static UniFiDeviceResponse CreateGatewayWithWan(string wanKey, string wanType)
    {
        var wanJson = JsonSerializer.Serialize(new { type = wanType, up = true, name = wanKey });
        var wanElement = JsonDocument.Parse(wanJson).RootElement.Clone();

        return new UniFiDeviceResponse
        {
            Id = "gateway1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Test Gateway",
            Type = "ugw",
            AdditionalData = new Dictionary<string, JsonElement>
            {
                [wanKey] = wanElement
            }
        };
    }

    private static UniFiDeviceResponse CreateModem(bool dataLimitEnabled, long dataLimitBytes)
    {
        var overridesJson = JsonSerializer.Serialize(new
        {
            primary_slot = 1,
            sim = new[]
            {
                new { slot = 1, data_limit_enabled = dataLimitEnabled, data_soft_limit_bytes = dataLimitBytes }
            }
        });
        var overridesElement = JsonDocument.Parse(overridesJson).RootElement.Clone();

        return new UniFiDeviceResponse
        {
            Id = "modem1",
            Mac = "aa:bb:cc:00:00:02",
            Name = "Test Modem",
            Type = "umbb",
            AdditionalData = new Dictionary<string, JsonElement>
            {
                ["mbb_overrides"] = overridesElement
            }
        };
    }

    private static UniFiDeviceResponse CreateSwitch(string id, string name)
    {
        return new UniFiDeviceResponse
        {
            Id = id,
            Mac = $"aa:bb:cc:00:00:{id.GetHashCode():x2}",
            Name = name,
            Type = "usw"
        };
    }

    private static UniFiDeviceResponse CreateSwitchWithPorts(params int[] speeds)
    {
        var device = CreateSwitch("switch1", "Switch 1");
        device.PortTable = speeds.Select((speed, i) => new SwitchPort
        {
            PortIdx = i + 1,
            Speed = speed,
            Up = true,
            IsUplink = false
        }).ToList();
        return device;
    }

    private static List<UniFiNetworkConfig> CreateWanNetwork(int downloadMbps)
    {
        return new List<UniFiNetworkConfig>
        {
            new()
            {
                Id = "wan-net-1",
                Name = "WAN",
                Purpose = "wan",
                WanProviderCapabilities = new WanProviderCapabilities
                {
                    DownloadKilobitsPerSecond = downloadMbps * 1000
                }
            }
        };
    }

    private static List<UniFiClientResponse> CreateWirelessClients(int count)
    {
        return Enumerable.Range(0, count).Select(i => new UniFiClientResponse
        {
            Mac = $"aa:bb:cc:dd:ee:{i:x2}",
            Name = $"iPhone {i}",
            IsWired = false
        }).ToList();
    }

    private static JsonDocument CreateSettings(
        bool jumboEnabled = false, bool flowCtrlEnabled = false, string[]? exclusions = null)
    {
        var globalSwitch = new Dictionary<string, object>
        {
            ["key"] = "global_switch",
            ["jumboframe_enabled"] = jumboEnabled,
            ["flowctrl_enabled"] = flowCtrlEnabled
        };

        if (exclusions != null)
            globalSwitch["switch_exclusions"] = exclusions;

        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            data = new object[] { globalSwitch }
        }));
    }

    private static JsonDocument CreateSettingsWithNetFlow(bool netflowEnabled)
    {
        var globalSwitch = new Dictionary<string, object>
        {
            ["key"] = "global_switch",
            ["jumboframe_enabled"] = false,
            ["flowctrl_enabled"] = false
        };

        var netflow = new Dictionary<string, object>
        {
            ["key"] = "netflow",
            ["enabled"] = netflowEnabled
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            data = new object[] { globalSwitch, netflow }
        }));
    }

    private static JsonDocument CreateEnrichedConfig(
        string wanKey, string networkGroup, string loadBalanceType, string configId)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            new
            {
                configuration = new
                {
                    _id = configId,
                    wan_networkgroup = networkGroup,
                    wan_load_balance_type = loadBalanceType,
                    purpose = "wan"
                }
            }
        }));
    }

    private static string CreateQosRuleJson(
        string name, string objective, bool enabled, int[] appIds, string? wanNetwork)
    {
        var rule = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["objective"] = objective,
            ["enabled"] = enabled,
            ["destination"] = new { app_ids = appIds, matching_target = "APP" }
        };
        if (wanNetwork != null)
            rule["wan_or_vpn_network"] = wanNetwork;

        return JsonSerializer.Serialize(rule);
    }

    private static JsonDocument CreateQosRulesDoc(string[] ruleJsons)
    {
        return JsonDocument.Parse($"[{string.Join(",", ruleJsons)}]");
    }

    private static JsonDocument CreateQosRulesForAllCategories(string wanId)
    {
        // Cover enough apps per category to meet thresholds
        var streamingIds = StreamingAppIds.StreamingVideo.Take(StreamingAppIds.MinStreamingForCoverage).ToArray();
        var cloudIds = StreamingAppIds.CloudStorage.Take(StreamingAppIds.MinCloudForCoverage).ToArray();
        var downloadIds = StreamingAppIds.LargeDownloads.Take(StreamingAppIds.MinDownloadsForCoverage).ToArray();

        return CreateQosRulesDoc(new[]
        {
            CreateQosRuleJson("Streaming", "LIMIT", true, streamingIds, wanId),
            CreateQosRuleJson("Cloud", "LIMIT", true, cloudIds, wanId),
            CreateQosRuleJson("Downloads", "LIMIT", true, downloadIds, wanId)
        });
    }

    #endregion
}
