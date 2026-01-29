using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class UpnpSecurityAnalyzerTests
{
    private readonly UpnpSecurityAnalyzer _analyzer;
    private readonly Mock<ILogger<UpnpSecurityAnalyzer>> _loggerMock;

    public UpnpSecurityAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<UpnpSecurityAnalyzer>>();
        _analyzer = new UpnpSecurityAnalyzer(_loggerMock.Object);
    }

    #region UPnP Status Tests

    [Fact]
    public void Analyze_UpnpStatusNull_ReturnsNoIssues()
    {
        // Arrange
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };

        // Act
        var result = _analyzer.Analyze(null, null, networks);

        // Assert
        result.Issues.Should().BeEmpty();
        result.HardeningNotes.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_UpnpDisabled_ReturnsHardeningNote()
    {
        // Arrange
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };

        // Act
        var result = _analyzer.Analyze(false, null, networks);

        // Assert
        result.Issues.Should().BeEmpty();
        result.HardeningNotes.Should().ContainSingle()
            .Which.Should().Contain("UPnP is disabled");
    }

    [Fact]
    public void Analyze_UpnpEnabledOnSingleHomeNetwork_ReturnsInformational()
    {
        // Arrange - Single Home network with UPnP enabled is acceptable (Informational)
        var networks = new List<NetworkInfo> { CreateNetwork("Home Network", NetworkPurpose.Home, upnpLanEnabled: true) };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert
        result.Issues.Should().ContainSingle();
        var issue = result.Issues[0];
        issue.Type.Should().Be(IssueTypes.UpnpEnabled);
        issue.Severity.Should().Be(AuditSeverity.Informational);
        issue.ScoreImpact.Should().Be(0);
        issue.Message.Should().Contain("Home Network");
    }

    [Fact]
    public void Analyze_UpnpEnabledOnSingleGamingNetwork_ReturnsInformational()
    {
        // Arrange - Single Gaming network (classified as Home) with UPnP is acceptable
        var networks = new List<NetworkInfo> { CreateNetwork("Gaming VLAN", NetworkPurpose.Home, upnpLanEnabled: true) };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert
        result.Issues.Should().ContainSingle();
        var issue = result.Issues[0];
        issue.Type.Should().Be(IssueTypes.UpnpEnabled);
        issue.Severity.Should().Be(AuditSeverity.Informational);
        issue.Message.Should().Contain("Gaming VLAN");
    }

    [Fact]
    public void Analyze_UpnpEnabledOnNonHomeNetwork_ReturnsCritical()
    {
        // Arrange - UPnP enabled on IoT network is Critical
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: false),
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, upnpLanEnabled: true)
        };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert
        result.Issues.Should().ContainSingle();
        var issue = result.Issues[0];
        issue.Type.Should().Be(IssueTypes.UpnpNonHomeNetwork);
        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.ScoreImpact.Should().Be(15);
        issue.Message.Should().Contain("IoT Devices");
        issue.Message.Should().Contain("IoT");
    }

    [Fact]
    public void Analyze_UpnpEnabledOnMultipleNonHomeNetworks_ReturnsCriticalListingAll()
    {
        // Arrange - UPnP enabled on multiple non-Home networks
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: false),
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, upnpLanEnabled: true),
            CreateNetwork("Work", NetworkPurpose.Corporate, upnpLanEnabled: true)
        };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert
        result.Issues.Should().ContainSingle();
        var issue = result.Issues[0];
        issue.Type.Should().Be(IssueTypes.UpnpNonHomeNetwork);
        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.Message.Should().Contain("IoT Devices");
        issue.Message.Should().Contain("Work");
    }

    [Fact]
    public void Analyze_UpnpEnabledOnBothHomeAndNonHomeNetworks_ReturnsBothIssues()
    {
        // Arrange - UPnP enabled on both Home and non-Home networks
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true),
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, upnpLanEnabled: true)
        };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert - Non-Home gets Critical, single Home gets Informational
        result.Issues.Should().HaveCount(2);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpNonHomeNetwork && i.Severity == AuditSeverity.Critical);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpEnabled && i.Severity == AuditSeverity.Informational);
    }

    [Fact]
    public void Analyze_UpnpGloballyEnabledButNotBoundToAnyNetwork_ReturnsHardeningNote()
    {
        // Arrange - UPnP is globally enabled but no networks have it bound
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: false),
            CreateNetwork("IoT", NetworkPurpose.IoT, upnpLanEnabled: false)
        };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert
        result.Issues.Should().BeEmpty();
        result.HardeningNotes.Should().ContainSingle()
            .Which.Should().Contain("not bound to any networks");
    }

    [Fact]
    public void Analyze_UpnpEnabledOnMultipleHomeNetworks_ReturnsRecommended()
    {
        // Arrange - Multiple Home networks with UPnP should recommend consolidating
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true),
            CreateNetwork("Gaming", NetworkPurpose.Home, upnpLanEnabled: true)
        };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert
        result.Issues.Should().ContainSingle();
        var issue = result.Issues[0];
        issue.Type.Should().Be(IssueTypes.UpnpEnabled);
        issue.Severity.Should().Be(AuditSeverity.Recommended);
        issue.ScoreImpact.Should().Be(5);
        issue.Message.Should().Contain("Home");
        issue.Message.Should().Contain("Gaming");
        issue.Message.Should().Contain("2 Home networks");
        issue.RecommendedAction.Should().Contain("one dedicated");
    }

    [Fact]
    public void Analyze_DisabledNetworkWithUpnp_StillFlagged()
    {
        // Arrange - Disabled network with UPnP should still trigger a warning
        // because the binding persists and will become active if the network is re-enabled
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, upnpLanEnabled: true, enabled: false)
        };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert - Should still flag the UPnP binding even though network is disabled
        result.Issues.Should().ContainSingle();
        var issue = result.Issues[0];
        issue.Type.Should().Be(IssueTypes.UpnpNonHomeNetwork);
        issue.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void Analyze_UpnpEnabledOnGuestNetwork_ReturnsCritical()
    {
        // Arrange - UPnP on Guest network is extremely dangerous
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Guest WiFi", NetworkPurpose.Guest, upnpLanEnabled: true)
        };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert
        result.Issues.Should().ContainSingle();
        var issue = result.Issues[0];
        issue.Type.Should().Be(IssueTypes.UpnpNonHomeNetwork);
        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.Message.Should().Contain("Guest WiFi");
        issue.Message.Should().Contain("Guest");
    }

    [Fact]
    public void Analyze_UpnpEnabledOnCorporateNetwork_ReturnsCritical()
    {
        // Arrange - UPnP on Corporate/Work network should be Critical
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Work", NetworkPurpose.Corporate, upnpLanEnabled: true)
        };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert
        result.Issues.Should().ContainSingle();
        var issue = result.Issues[0];
        issue.Type.Should().Be(IssueTypes.UpnpNonHomeNetwork);
        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.Message.Should().Contain("Work");
        issue.Message.Should().Contain("Corporate");
    }

    [Fact]
    public void Analyze_UpnpEnabledOnSecurityNetwork_ReturnsCritical()
    {
        // Arrange - UPnP on Security/Camera network should be Critical
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Cameras", NetworkPurpose.Security, upnpLanEnabled: true)
        };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert
        result.Issues.Should().ContainSingle();
        var issue = result.Issues[0];
        issue.Type.Should().Be(IssueTypes.UpnpNonHomeNetwork);
        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.Message.Should().Contain("Cameras");
        issue.Message.Should().Contain("Security");
    }

    #endregion

    #region UPnP Port Mapping Tests

    [Fact]
    public void Analyze_UpnpMappingsWithOnlyPrivilegedPorts_NoPortsExposedInfo()
    {
        // Arrange - All ports are privileged, so no redundant "Ports Exposed" info
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateUpnpRule("80", "Web Server"),
            CreateUpnpRule("443", "HTTPS")
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert
        result.Issues.Should().HaveCount(2); // UpnpEnabled + UpnpPrivilegedPort (NO UpnpPortsExposed)
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpPrivilegedPort);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.UpnpPortsExposed);
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.UpnpPrivilegedPort);
        privIssue.Severity.Should().Be(AuditSeverity.Recommended);
        privIssue.ScoreImpact.Should().Be(8);
        privIssue.Message.Should().Contain("80/HTTP");
        privIssue.Message.Should().Contain("443/HTTPS");
    }

    [Fact]
    public void Analyze_UpnpMappingsWithMixedPorts_ReportsBoth()
    {
        // Arrange - Mix of privileged and non-privileged ports
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateUpnpRule("80", "Web Server"),
            CreateUpnpRule("3074", "Xbox Live")
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should have both warning for privileged AND info for non-privileged
        result.Issues.Should().HaveCount(3); // UpnpEnabled + UpnpPrivilegedPort + UpnpPortsExposed
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpPrivilegedPort);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpPortsExposed);
    }

    [Fact]
    public void Analyze_UpnpMappingsWithNonPrivilegedPorts_ReturnsInfo()
    {
        // Arrange
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateUpnpRule("3074", "Xbox Live"),
            CreateUpnpRule("27015", "Steam")
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert
        result.Issues.Should().HaveCount(2); // UpnpEnabled + UpnpPortsExposed
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpPortsExposed);
        var portsIssue = result.Issues.First(i => i.Type == IssueTypes.UpnpPortsExposed);
        portsIssue.Severity.Should().Be(AuditSeverity.Informational);
        portsIssue.ScoreImpact.Should().Be(0);
    }

    [Fact]
    public void Analyze_UpnpMappingsWithPortRange_DetectsPrivilegedPorts()
    {
        // Arrange - Port range 50-100 includes privileged ports
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateUpnpRule("50-100", "Port Range")
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpPrivilegedPort);
    }

    [Fact]
    public void Analyze_UpnpMappingsWithCommaList_ParsesAllPorts()
    {
        // Arrange - Comma-separated ports including privileged
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateUpnpRule("80,443,8080", "Multiple Ports")
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpPrivilegedPort);
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.UpnpPrivilegedPort);
        privIssue.Message.Should().Contain("80");
        privIssue.Message.Should().Contain("443");
    }

    [Fact]
    public void Analyze_NoUpnpMappings_NoPortIssues()
    {
        // Arrange
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.UpnpPortsExposed);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.UpnpPrivilegedPort);
    }

    #endregion

    #region Static Port Forward Tests

    [Fact]
    public void Analyze_StaticPortForwardsNonPrivileged_ReturnsInformational()
    {
        // Arrange - Non-privileged ports only
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("8080", "Web Proxy", enabled: true),
            CreateStaticRule("25565", "Game Server", enabled: true)
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should have StaticPortForward, NOT StaticPrivilegedPort
        result.Issues.Should().Contain(i => i.Type == IssueTypes.StaticPortForward);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.StaticPrivilegedPort);
        var staticIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPortForward);
        staticIssue.Severity.Should().Be(AuditSeverity.Informational);
        staticIssue.ScoreImpact.Should().Be(0);
        staticIssue.Message.Should().Contain("2 static port forward");
    }

    [Fact]
    public void Analyze_StaticPortForwardsOnlyPrivileged_NoGenericInfo()
    {
        // Arrange - Only privileged ports, no generic "Static Rules" info needed
        // Use restricted rules (with src_limiting_enabled and firewall group) to get Informational severity
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("80", "Web Server", enabled: true,
                srcLimitingEnabled: true, srcLimitingType: "firewall_group", srcFirewallGroupId: "trusted-ips"),
            CreateStaticRule("443", "HTTPS Server", enabled: true,
                srcLimitingEnabled: true, srcLimitingType: "firewall_group", srcFirewallGroupId: "trusted-ips")
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should have StaticPrivilegedPort, NOT StaticPortForward
        result.Issues.Should().Contain(i => i.Type == IssueTypes.StaticPrivilegedPort);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.StaticPortForward);
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPrivilegedPort);
        privIssue.Severity.Should().Be(AuditSeverity.Informational);
        privIssue.Message.Should().Contain("80/HTTP");
        privIssue.Message.Should().Contain("443/HTTPS");
    }

    [Fact]
    public void Analyze_PrivilegedPorts_HomeNetwork_Unrestricted_ReturnsWarning()
    {
        // Arrange - Privileged ports without source IP restriction on Home network
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("22", "SSH", enabled: true),  // No srcFirewallGroupId
            CreateStaticRule("443", "HTTPS", enabled: true)  // No srcFirewallGroupId
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should be Warning (Recommended) with source IP recommendation
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPrivilegedPort);
        privIssue.Severity.Should().Be(AuditSeverity.Recommended);
        privIssue.ScoreImpact.Should().Be(8);
        privIssue.RecommendedAction.Should().Contain("source IP");
        privIssue.Metadata!["unrestricted"].Should().Be(true);
    }

    [Fact]
    public void Analyze_PrivilegedPorts_HomeNetwork_Restricted_FirewallGroup_ReturnsInfo()
    {
        // Arrange - Privileged ports WITH source firewall group restriction on Home network
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("22", "SSH", enabled: true,
                srcLimitingEnabled: true, srcLimitingType: "firewall_group", srcFirewallGroupId: "work-vpn"),
            CreateStaticRule("443", "HTTPS", enabled: true,
                srcLimitingEnabled: true, srcLimitingType: "firewall_group", srcFirewallGroupId: "trusted-ips")
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should be Informational (properly secured)
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPrivilegedPort);
        privIssue.Severity.Should().Be(AuditSeverity.Informational);
        privIssue.ScoreImpact.Should().Be(0);
        privIssue.Metadata!["unrestricted"].Should().Be(false);
    }

    [Fact]
    public void Analyze_PrivilegedPorts_HomeNetwork_Restricted_IpAddress_ReturnsInfo()
    {
        // Arrange - Privileged ports WITH source IP restriction on Home network
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("22", "SSH", enabled: true,
                srcLimitingEnabled: true, srcLimitingType: "ip", src: "10.0.0.0/24"),
            CreateStaticRule("443", "HTTPS", enabled: true,
                srcLimitingEnabled: true, srcLimitingType: "ip", src: "192.168.1.100")
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should be Informational (properly secured with IP restriction)
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPrivilegedPort);
        privIssue.Severity.Should().Be(AuditSeverity.Informational);
        privIssue.ScoreImpact.Should().Be(0);
        privIssue.Metadata!["unrestricted"].Should().Be(false);
    }

    [Fact]
    public void Analyze_PrivilegedPorts_HomeNetwork_OrphanedFirewallGroup_ReturnsWarning()
    {
        // Arrange - src_firewall_group_id is set but src_limiting_enabled is false (orphaned)
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("22", "SSH", enabled: true,
                srcLimitingEnabled: false, srcLimitingType: "firewall_group", srcFirewallGroupId: "old-group-id")
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should be Warning because limiting is disabled (orphaned config)
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPrivilegedPort);
        privIssue.Severity.Should().Be(AuditSeverity.Recommended);
        privIssue.ScoreImpact.Should().Be(8);
        privIssue.Metadata!["unrestricted"].Should().Be(true);
    }

    [Fact]
    public void Analyze_PrivilegedPorts_HomeNetwork_LimitingEnabledButNoGroup_ReturnsWarning()
    {
        // Arrange - src_limiting_enabled is true but src_firewall_group_id is empty
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("22", "SSH", enabled: true,
                srcLimitingEnabled: true, srcLimitingType: "firewall_group", srcFirewallGroupId: null)
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should be Warning because no valid group ID
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPrivilegedPort);
        privIssue.Severity.Should().Be(AuditSeverity.Recommended);
        privIssue.ScoreImpact.Should().Be(8);
    }

    [Fact]
    public void Analyze_PrivilegedPorts_HomeNetwork_IpTypeButNoSrc_ReturnsWarning()
    {
        // Arrange - src_limiting_type is "ip" but src is empty
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("22", "SSH", enabled: true,
                srcLimitingEnabled: true, srcLimitingType: "ip", src: null)
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should be Warning because no valid src IP
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPrivilegedPort);
        privIssue.Severity.Should().Be(AuditSeverity.Recommended);
        privIssue.ScoreImpact.Should().Be(8);
    }

    [Fact]
    public void Analyze_PrivilegedPorts_CorporateNetwork_Unrestricted_ReturnsInfo()
    {
        // Arrange - Privileged ports without source restriction on Corporate network
        // Corporate networks don't trigger the warning since UPnP wouldn't be typical there
        var networks = new List<NetworkInfo> { CreateNetwork("Corporate", NetworkPurpose.Corporate) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("22", "SSH", enabled: true),  // No srcFirewallGroupId
            CreateStaticRule("443", "HTTPS", enabled: true)  // No srcFirewallGroupId
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should be Informational (no Home network to trigger warning)
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPrivilegedPort);
        privIssue.Severity.Should().Be(AuditSeverity.Informational);
        privIssue.ScoreImpact.Should().Be(0);
    }

    [Fact]
    public void Analyze_PrivilegedPorts_CorporateNetwork_Restricted_ReturnsInfo()
    {
        // Arrange - Privileged ports WITH source restriction on Corporate network
        var networks = new List<NetworkInfo> { CreateNetwork("Corporate", NetworkPurpose.Corporate) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("22", "SSH", enabled: true,
                srcLimitingEnabled: true, srcLimitingType: "firewall_group", srcFirewallGroupId: "office-ips"),
            CreateStaticRule("443", "HTTPS", enabled: true,
                srcLimitingEnabled: true, srcLimitingType: "firewall_group", srcFirewallGroupId: "office-ips")
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should be Informational
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPrivilegedPort);
        privIssue.Severity.Should().Be(AuditSeverity.Informational);
        privIssue.ScoreImpact.Should().Be(0);
    }

    [Fact]
    public void Analyze_PrivilegedPorts_MixedRestricted_HomeNetwork_ReturnsWarning()
    {
        // Arrange - Some ports restricted, some not, on Home network
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("22", "SSH", enabled: true,
                srcLimitingEnabled: true, srcLimitingType: "firewall_group", srcFirewallGroupId: "work-vpn"),  // Restricted
            CreateStaticRule("443", "HTTPS", enabled: true)  // NOT restricted
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should be Warning because at least one is unrestricted
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPrivilegedPort);
        privIssue.Severity.Should().Be(AuditSeverity.Recommended);
        privIssue.ScoreImpact.Should().Be(8);
        privIssue.Metadata!["unrestricted_count"].Should().Be(1);
    }

    [Fact]
    public void Analyze_StaticPortForwardsMixedPorts_ReportsBoth()
    {
        // Arrange - Mix of privileged and non-privileged
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("80", "Web Server", enabled: true),
            CreateStaticRule("8080", "Web Proxy", enabled: true)
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should have BOTH StaticPrivilegedPort AND StaticPortForward
        result.Issues.Should().Contain(i => i.Type == IssueTypes.StaticPrivilegedPort);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.StaticPortForward);
    }

    [Fact]
    public void Analyze_StaticPrivilegedPorts_ShowsServiceNames()
    {
        // Arrange - Various well-known ports
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("22", "SSH Access", enabled: true),
            CreateStaticRule("25", "Mail Server", enabled: true),
            CreateStaticRule("53", "DNS Server", enabled: true)
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - Should show service names
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.StaticPrivilegedPort);
        privIssue.Message.Should().Contain("22/SSH");
        privIssue.Message.Should().Contain("25/SMTP");
        privIssue.Message.Should().Contain("53/DNS");
    }

    [Fact]
    public void Analyze_DisabledStaticPortForwards_NotReported()
    {
        // Arrange - Disabled static rules should not be reported
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("8080", "Disabled Server", enabled: false)
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.StaticPortForward);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.StaticPrivilegedPort);
    }

    [Fact]
    public void Analyze_MixedUpnpAndStatic_ReportsBoth()
    {
        // Arrange
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateUpnpRule("3074", "Xbox Live"),
            CreateStaticRule("25565", "Minecraft", enabled: true)
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpEnabled);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpPortsExposed);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.StaticPortForward);
    }

    [Fact]
    public void Analyze_UpnpDisabled_StillReportsStaticForwards()
    {
        // Arrange - UPnP disabled but static forwards present
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateStaticRule("80", "Web Server", enabled: true),
            CreateStaticRule("8080", "Web Proxy", enabled: true)
        };

        // Act
        var result = _analyzer.Analyze(false, rules, networks);

        // Assert - Should still analyze static forwards
        result.HardeningNotes.Should().ContainSingle().Which.Should().Contain("UPnP is disabled");
        result.Issues.Should().Contain(i => i.Type == IssueTypes.StaticPrivilegedPort);
        result.Issues.Should().Contain(i => i.Type == IssueTypes.StaticPortForward);
    }

    [Fact]
    public void Analyze_UpnpDisabled_NoUpnpIssues()
    {
        // Arrange - UPnP disabled with UPnP rules present (should be ignored)
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home) };
        var rules = new List<UniFiPortForwardRule>
        {
            CreateUpnpRule("80", "Web Server"),
            CreateStaticRule("8080", "Proxy", enabled: true)
        };

        // Act
        var result = _analyzer.Analyze(false, rules, networks);

        // Assert - UPnP rules should not be reported when disabled
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.UpnpEnabled);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.UpnpPrivilegedPort);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.UpnpPortsExposed);
        // But static should still be there
        result.Issues.Should().Contain(i => i.Type == IssueTypes.StaticPortForward);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Analyze_EmptyNetworkList_HandlesGracefully()
    {
        // Arrange - Empty network list means no networks have UPnP binding
        var networks = new List<NetworkInfo>();

        // Act
        var result = _analyzer.Analyze(true, new List<UniFiPortForwardRule>(), networks);

        // Assert - No issues, just a hardening note that UPnP isn't bound to any networks
        result.Issues.Should().BeEmpty();
        result.HardeningNotes.Should().ContainSingle()
            .Which.Should().Contain("not bound to any networks");
    }

    [Fact]
    public void Analyze_NullPortForwardRules_HandlesGracefully()
    {
        // Arrange
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };

        // Act
        var result = _analyzer.Analyze(true, null, networks);

        // Assert
        result.Issues.Should().ContainSingle();
        result.Issues[0].Type.Should().Be(IssueTypes.UpnpEnabled);
    }

    [Fact]
    public void Analyze_RuleWithEmptyPort_SkipsRule()
    {
        // Arrange
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };
        var rules = new List<UniFiPortForwardRule>
        {
            new UniFiPortForwardRule { IsUpnp = 1, DstPort = "", Name = "Empty Port" },
            new UniFiPortForwardRule { IsUpnp = 1, DstPort = null, Name = "Null Port" }
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert - UPnP issues should exist but no port-specific issues
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpEnabled);
        result.Issues.Should().NotContain(i => i.Type == IssueTypes.UpnpPortsExposed);
    }

    [Fact]
    public void Analyze_ApplicationNameExtracted_ShowsInMessage()
    {
        // Arrange
        var networks = new List<NetworkInfo> { CreateNetwork("Home", NetworkPurpose.Home, upnpLanEnabled: true) };
        var rules = new List<UniFiPortForwardRule>
        {
            new UniFiPortForwardRule
            {
                IsUpnp = 1,
                DstPort = "80",
                Name = "UPnP [Sunshine - RTSP]"  // ApplicationName will extract "Sunshine - RTSP"
            }
        };

        // Act
        var result = _analyzer.Analyze(true, rules, networks);

        // Assert
        result.Issues.Should().Contain(i => i.Type == IssueTypes.UpnpPrivilegedPort);
        var privIssue = result.Issues.First(i => i.Type == IssueTypes.UpnpPrivilegedPort);
        privIssue.Message.Should().Contain("Sunshine");
    }

    #endregion

    #region Helper Methods

    private static NetworkInfo CreateNetwork(
        string name,
        NetworkPurpose purpose,
        int vlanId = 10,
        bool upnpLanEnabled = false,
        bool enabled = true)
    {
        return new NetworkInfo
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            VlanId = vlanId,
            Purpose = purpose,
            Subnet = $"192.168.{vlanId}.0/24",
            Gateway = $"192.168.{vlanId}.1",
            UpnpLanEnabled = upnpLanEnabled,
            Enabled = enabled
        };
    }

    private static UniFiPortForwardRule CreateUpnpRule(string port, string name)
    {
        return new UniFiPortForwardRule
        {
            IsUpnp = 1,
            DstPort = port,
            Name = $"UPnP [{name}]",
            Fwd = "192.168.1.100",
            Proto = "udp"
        };
    }

    private static UniFiPortForwardRule CreateStaticRule(
        string port,
        string name,
        bool enabled,
        bool? srcLimitingEnabled = null,
        string? srcLimitingType = null,
        string? srcFirewallGroupId = null,
        string? src = null)
    {
        return new UniFiPortForwardRule
        {
            IsUpnp = 0,
            DstPort = port,
            Name = name,
            Fwd = "192.168.1.100",
            Proto = "tcp",
            Enabled = enabled,
            SrcLimitingEnabled = srcLimitingEnabled,
            SrcLimitingType = srcLimitingType,
            SrcFirewallGroupId = srcFirewallGroupId,
            Src = src
        };
    }

    #endregion
}
