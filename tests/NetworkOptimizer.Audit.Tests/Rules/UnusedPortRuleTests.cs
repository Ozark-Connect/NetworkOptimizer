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

    #region Evaluate Tests - Port With Custom Name (Recently Active)

    [Fact]
    public void Evaluate_PortDownWithCustomName_RecentlyActive_ReturnsNull()
    {
        // Arrange - Custom-named port that was recently active (within 45 days)
        var recentTimestamp = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Printer", isUp: false, forwardMode: "native", lastConnectionSeen: recentTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull("custom-named port active within 45 days should not be flagged");
    }

    [Fact]
    public void Evaluate_PortDownWithDescriptiveName_RecentlyActive_ReturnsNull()
    {
        // Arrange
        var recentTimestamp = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Server Room Camera", isUp: false, forwardMode: "native", lastConnectionSeen: recentTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull("custom-named port active within 45 days should not be flagged");
    }

    [Fact]
    public void Evaluate_PortDownWithWorkstationName_RecentlyActive_ReturnsNull()
    {
        // Arrange
        var recentTimestamp = DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeSeconds();
        var port = CreatePort(portName: "John's Workstation", isUp: false, forwardMode: "native", lastConnectionSeen: recentTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull("custom-named port active within 45 days should not be flagged");
    }

    [Fact]
    public void Evaluate_PortDownWithCustomName_OldActivity_ReturnsIssue()
    {
        // Arrange - Custom-named port that's been inactive for over 45 days
        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-50).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Printer", isUp: false, forwardMode: "native", lastConnectionSeen: oldTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull("custom-named port inactive for >45 days should be flagged");
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
    public void Evaluate_VariousCustomNames_RecentlyActive_ReturnsNull(string portName)
    {
        // Arrange - Custom-named port with recent activity (within 45 days)
        var recentTimestamp = DateTimeOffset.UtcNow.AddDays(-20).ToUnixTimeSeconds();
        var port = CreatePort(portName: portName, isUp: false, forwardMode: "native", lastConnectionSeen: recentTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull($"Port with custom name '{portName}' recently active should not be flagged");
    }

    [Theory]
    [InlineData("Printer")]
    [InlineData("Camera")]
    [InlineData("Server 1")]
    public void Evaluate_VariousCustomNames_OldActivity_ReturnsIssue(string portName)
    {
        // Arrange - Custom-named port with old activity (>45 days)
        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeSeconds();
        var port = CreatePort(portName: portName, isUp: false, forwardMode: "native", lastConnectionSeen: oldTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull($"Port with custom name '{portName}' inactive >45 days should be flagged");
    }

    #endregion

    #region Evaluate Tests - Time-Based Thresholds

    [Fact]
    public void Evaluate_UnnamedPort_ActiveWithin15Days_ReturnsNull()
    {
        // Arrange - Unnamed port with recent activity (within 15 days)
        var recentTimestamp = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Port 5", isUp: false, forwardMode: "native", lastConnectionSeen: recentTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull("unnamed port active within 15 days should not be flagged");
    }

    [Fact]
    public void Evaluate_UnnamedPort_InactiveOver15Days_ReturnsIssue()
    {
        // Arrange - Unnamed port inactive for >15 days
        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-20).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Port 5", isUp: false, forwardMode: "native", lastConnectionSeen: oldTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull("unnamed port inactive >15 days should be flagged");
    }

    [Fact]
    public void Evaluate_NamedPort_ActiveWithin45Days_ReturnsNull()
    {
        // Arrange - Named port with activity within 45 days (but over 15 days)
        var timestamp = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Printer", isUp: false, forwardMode: "native", lastConnectionSeen: timestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull("named port active within 45 days should not be flagged");
    }

    [Fact]
    public void Evaluate_NamedPort_InactiveOver45Days_ReturnsIssue()
    {
        // Arrange - Named port inactive for >45 days
        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-50).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Printer", isUp: false, forwardMode: "native", lastConnectionSeen: oldTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull("named port inactive >45 days should be flagged");
    }

    [Fact]
    public void Evaluate_PortWithNoLastConnectionSeen_ReturnsIssue()
    {
        // Arrange - Port with no last connection data
        var port = CreatePort(portName: "Port 5", isUp: false, forwardMode: "native", lastConnectionSeen: null);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull("port with no last connection data should be flagged");
    }

    #endregion

    #region Evaluate Tests - Configurable Thresholds

    [Fact]
    public void SetThresholds_ChangesUnnamedPortThreshold()
    {
        // Arrange - Set a short 5-day threshold for unnamed ports
        UnusedPortRule.SetThresholds(unusedPortDays: 5, namedPortDays: 45);
        var timestamp = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Port 5", isUp: false, forwardMode: "native", lastConnectionSeen: timestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull("port inactive >5 days should be flagged with custom threshold");

        // Cleanup - Reset to defaults
        UnusedPortRule.SetThresholds(15, 45);
    }

    [Fact]
    public void SetThresholds_ChangesNamedPortThreshold()
    {
        // Arrange - Set a short 10-day threshold for named ports
        UnusedPortRule.SetThresholds(unusedPortDays: 15, namedPortDays: 10);
        var timestamp = DateTimeOffset.UtcNow.AddDays(-12).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Printer", isUp: false, forwardMode: "native", lastConnectionSeen: timestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull("named port inactive >10 days should be flagged with custom threshold");

        // Cleanup - Reset to defaults
        UnusedPortRule.SetThresholds(15, 45);
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
        string switchName = "Test Switch",
        long? lastConnectionSeen = null)
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
            Switch = switchInfo,
            LastConnectionSeen = lastConnectionSeen
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
