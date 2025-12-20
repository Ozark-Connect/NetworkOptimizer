using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class MacRestrictionRuleTests
{
    private readonly MacRestrictionRule _rule;

    public MacRestrictionRuleTests()
    {
        _rule = new MacRestrictionRule();
    }

    #region Rule Properties

    [Fact]
    public void RuleId_ReturnsExpectedValue()
    {
        _rule.RuleId.Should().Be("MAC-RESTRICT-001");
    }

    [Fact]
    public void Severity_IsRecommended()
    {
        _rule.Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void ScoreImpact_Is3()
    {
        _rule.ScoreImpact.Should().Be(3);
    }

    #endregion

    #region Evaluate Tests - Ports That Should Be Ignored

    [Fact]
    public void Evaluate_PortNotUp_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: false, forwardMode: "native");

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_TrunkPort_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "all");

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_UplinkPort_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "native", isUplink: true);

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WanPort_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "native", isWan: true);

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SwitchDoesNotSupportMacAcls_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "native", maxMacAcls: 0);

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Ports That Are Already Protected

    [Fact]
    public void Evaluate_PortSecurityEnabled_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "native", portSecurityEnabled: true);

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_HasAllowedMacAddresses_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(
            isUp: true,
            forwardMode: "native",
            allowedMacs: new List<string> { "AA:BB:CC:DD:EE:FF" });

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Ports That Should Trigger Issue

    [Fact]
    public void Evaluate_UnprotectedAccessPort_ReturnsIssue()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "native");

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("MAC-RESTRICT-001");
        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
    }

    [Fact]
    public void Evaluate_UnprotectedAccessPort_IncludesPortDetails()
    {
        // Arrange
        var port = CreatePort(
            isUp: true,
            forwardMode: "native",
            portIndex: 5,
            portName: "Office PC",
            switchName: "Switch-Lobby");

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().NotBeNull();
        result!.DeviceName.Should().Be("Switch-Lobby");
        result.Port.Should().Be("5");
        result.PortName.Should().Be("Office PC");
    }

    [Fact]
    public void Evaluate_UnprotectedAccessPort_IncludesNetworkName()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-123", Name = "Corporate LAN", VlanId = 10 }
        };
        var port = CreatePort(
            isUp: true,
            forwardMode: "native",
            nativeNetworkId: "net-123");

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("network");
        result.Metadata!["network"].Should().Be("Corporate LAN");
    }

    #endregion

    #region Helper Methods

    private static PortInfo CreatePort(
        bool isUp = true,
        string forwardMode = "native",
        bool isUplink = false,
        bool isWan = false,
        bool portSecurityEnabled = false,
        List<string>? allowedMacs = null,
        int maxMacAcls = 32,
        int portIndex = 1,
        string portName = "Port 1",
        string switchName = "Test Switch",
        string? nativeNetworkId = null)
    {
        var switchInfo = new SwitchInfo
        {
            Name = switchName,
            Capabilities = new SwitchCapabilities
            {
                MaxCustomMacAcls = maxMacAcls
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
            PortSecurityEnabled = portSecurityEnabled,
            AllowedMacAddresses = allowedMacs,
            NativeNetworkId = nativeNetworkId,
            Switch = switchInfo
        };
    }

    #endregion
}
