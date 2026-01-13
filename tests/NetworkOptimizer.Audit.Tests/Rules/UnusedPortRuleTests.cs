using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using Xunit;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class UnusedPortRuleTests
{
    private readonly UnusedPortRule _rule;

    public UnusedPortRuleTests()
    {
        _rule = new UnusedPortRule();
    }

    #region Rule Properties

    [Fact]
    public void RuleId_ReturnsExpectedValue()
    {
        _rule.RuleId.Should().Be("UNUSED-PORT-001");
    }

    [Fact]
    public void RuleName_ReturnsExpectedValue()
    {
        _rule.RuleName.Should().Be("Unused Port Disabled");
    }

    [Fact]
    public void Severity_IsRecommended()
    {
        _rule.Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void ScoreImpact_Is2()
    {
        _rule.ScoreImpact.Should().Be(2);
    }

    #endregion

    #region Evaluate Tests - Ports That Are Up

    [Fact]
    public void Evaluate_PortUp_ReturnsNull()
    {
        // Arrange - Active ports should not be flagged
        var port = CreatePort(isUp: true, forwardMode: "native");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_PortUpWithDefaultName_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(portName: "Port 1", isUp: true, forwardMode: "native");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Skip Uplink and WAN

    [Fact]
    public void Evaluate_UplinkPort_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: false, isUplink: true);
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
        var port = CreatePort(isUp: false, isWan: true);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Port Already Disabled

    [Fact]
    public void Evaluate_PortDisabled_ReturnsNull()
    {
        // Arrange - Correctly disabled port
        var port = CreatePort(isUp: false, forwardMode: "disabled");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_PortDisabledWithDefaultName_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(portName: "Port 5", isUp: false, forwardMode: "disabled");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Port With Custom Name

    [Fact]
    public void Evaluate_PortDownWithCustomName_ReturnsNull()
    {
        // Arrange - Custom-named port that's down might just be a device that's off
        var port = CreatePort(portName: "Printer", isUp: false, forwardMode: "native");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_PortDownWithDescriptiveName_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(portName: "Server Room Camera", isUp: false, forwardMode: "native");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_PortDownWithWorkstationName_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(portName: "John's Workstation", isUp: false, forwardMode: "native");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Unused Port Not Disabled (Issues)

    [Fact]
    public void Evaluate_UnnamedPortDownNotDisabled_ReturnsIssue()
    {
        // Arrange - Unnamed port that's down and not disabled should be flagged
        var port = CreatePort(portName: null, isUp: false, forwardMode: "native");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("UNUSED-PORT-001");
        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(2);
        result.Message.Should().Contain("disabled");
    }

    [Fact]
    public void Evaluate_DefaultNamedPortDownNotDisabled_ReturnsIssue()
    {
        // Arrange - Default "Port X" name with port down and not disabled
        var port = CreatePort(portName: "Port 5", isUp: false, forwardMode: "native");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("UNUSED-PORT-001");
    }

    [Fact]
    public void Evaluate_SfpPortDownNotDisabled_ReturnsIssue()
    {
        // Arrange - Default "SFP X" name with port down and not disabled
        var port = CreatePort(portName: "SFP 1", isUp: false, forwardMode: "native");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("UNUSED-PORT-001");
    }

    [Fact]
    public void Evaluate_SfpPlusPortDownNotDisabled_ReturnsIssue()
    {
        // Arrange - Default "SFP+ X" name with port down and not disabled
        var port = CreatePort(portName: "SFP+ 2", isUp: false, forwardMode: "native");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("UNUSED-PORT-001");
    }

    [Fact]
    public void Evaluate_PortDownWithAllForwardMode_ReturnsIssue()
    {
        // Arrange - Trunk port that's down
        var port = CreatePort(portName: "Port 10", isUp: false, forwardMode: "all");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
    }

    #endregion

    #region Evaluate Tests - Issue Details

    [Fact]
    public void Evaluate_IssueIncludesMetadata()
    {
        // Arrange
        var port = CreatePort(
            portIndex: 8,
            portName: "Port 8",
            isUp: false,
            forwardMode: "native",
            switchName: "Office Switch");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("current_forward_mode");
        result.Metadata!["current_forward_mode"].Should().Be("native");
        result.Metadata.Should().ContainKey("recommendation");
    }

    [Fact]
    public void Evaluate_IssueIncludesPortDetails()
    {
        // Arrange
        var port = CreatePort(
            portIndex: 15,
            portName: "Port 15",
            isUp: false,
            forwardMode: "native",
            switchName: "Server Room Switch");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Port.Should().Be("15");
        result.PortName.Should().Be("Port 15");
        result.DeviceName.Should().Contain("Server Room Switch");
    }

    #endregion

    #region Evaluate Tests - Default Port Name Pattern Matching

    [Theory]
    [InlineData("Port 1")]
    [InlineData("Port 10")]
    [InlineData("Port 24")]
    [InlineData("port 5")]  // Case insensitive
    [InlineData("PORT 8")]
    [InlineData("SFP 1")]
    [InlineData("SFP 2")]
    [InlineData("sfp 1")]
    [InlineData("SFP+ 1")]
    [InlineData("SFP+1")]
    public void Evaluate_VariousDefaultNames_ReturnsIssue(string portName)
    {
        // Arrange
        var port = CreatePort(portName: portName, isUp: false, forwardMode: "native");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull($"Port with default name '{portName}' should be flagged");
    }

    [Theory]
    [InlineData("Printer")]
    [InlineData("Camera")]
    [InlineData("Server 1")]
    [InlineData("AP-Lobby")]
    [InlineData("John's PC")]
    [InlineData("Meeting Room Display")]
    public void Evaluate_VariousCustomNames_ReturnsNull(string portName)
    {
        // Arrange
        var port = CreatePort(portName: portName, isUp: false, forwardMode: "native");
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull($"Port with custom name '{portName}' should not be flagged");
    }

    #endregion

    #region Helper Methods

    private static PortInfo CreatePort(
        int portIndex = 1,
        string? portName = null,
        bool isUp = true,
        string? forwardMode = "native",
        bool isUplink = false,
        bool isWan = false,
        string? networkId = "default-net",
        string switchName = "Test Switch")
    {
        var switchInfo = new SwitchInfo
        {
            Name = switchName,
            Model = "USW-24",
            Type = "usw"
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
