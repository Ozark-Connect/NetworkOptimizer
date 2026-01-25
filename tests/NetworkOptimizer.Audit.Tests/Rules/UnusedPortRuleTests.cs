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

    #region Evaluate Tests - Invalid Timestamps (GitHub Issue #154)

    [Fact]
    public void Evaluate_InvalidTimestamp_VeryOld_ReturnsNull()
    {
        // Arrange - Timestamp from 1986 (clearly invalid UniFi API data)
        // This is the scenario from GitHub issue #154: lastSeen=509086767
        const long invalidTimestamp = 509086767; // Feb 18, 1986
        var port = CreatePort(portName: "Port 4", isUp: false, forwardMode: "native", lastConnectionSeen: invalidTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should NOT flag port when timestamp is clearly invalid
        result.Should().BeNull("port with invalid timestamp (>10 years old) should not be flagged");
    }

    [Fact]
    public void Evaluate_InvalidTimestamp_ZeroEpoch_ReturnsNull()
    {
        // Arrange - Unix epoch (Jan 1, 1970) is clearly invalid
        const long epochTimestamp = 0;
        var port = CreatePort(portName: "Port 5", isUp: false, forwardMode: "native", lastConnectionSeen: epochTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull("port with epoch timestamp should not be flagged");
    }

    [Fact]
    public void Evaluate_InvalidTimestamp_Year2000_ReturnsNull()
    {
        // Arrange - Year 2000 timestamp (before UniFi existed)
        var year2000Timestamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Port 6", isUp: false, forwardMode: "native", lastConnectionSeen: year2000Timestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull("port with timestamp from year 2000 should not be flagged");
    }

    [Fact]
    public void Evaluate_ValidOldTimestamp_JustUnderThreshold_ReturnsNull()
    {
        // Arrange - Timestamp that's old but within the 10-year reasonable window
        // and within the named port threshold (45 days)
        var validOldTimestamp = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Printer", isUp: false, forwardMode: "native", lastConnectionSeen: validOldTimestamp);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should NOT flag because it's within the 45-day threshold for named ports
        result.Should().BeNull("named port active within 45 days should not be flagged");
    }

    [Fact]
    public void Evaluate_ValidOldTimestamp_BeyondThreshold_ReturnsIssue()
    {
        // Arrange - Timestamp from 1 year ago (valid but beyond threshold)
        var oneYearAgo = DateTimeOffset.UtcNow.AddDays(-365).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Port 7", isUp: false, forwardMode: "native", lastConnectionSeen: oneYearAgo);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should flag because timestamp is valid and beyond threshold
        result.Should().NotBeNull("port with valid 1-year-old timestamp should be flagged");
    }

    [Fact]
    public void Evaluate_ValidOldTimestamp_FiveYearsAgo_ReturnsIssue()
    {
        // Arrange - Timestamp from 5 years ago (valid, within 10-year window)
        var fiveYearsAgo = DateTimeOffset.UtcNow.AddYears(-5).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Port 8", isUp: false, forwardMode: "native", lastConnectionSeen: fiveYearsAgo);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Should flag because it's a valid old timestamp
        result.Should().NotBeNull("port with valid 5-year-old timestamp should be flagged");
    }

    [Fact]
    public void Evaluate_ValidTimestamp_JustUnderBoundary_ReturnsIssue()
    {
        // Arrange - Timestamp just under the 10-year boundary (should be flagged as legitimately old)
        var justUnderTenYears = DateTimeOffset.UtcNow.AddDays(-3649).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Port 9", isUp: false, forwardMode: "native", lastConnectionSeen: justUnderTenYears);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Just under 3650 days is still valid and should be flagged as unused
        result.Should().NotBeNull("port just under 10-year boundary should be flagged as legitimately unused");
    }

    [Fact]
    public void Evaluate_InvalidTimestamp_JustPastBoundary_ReturnsNull()
    {
        // Arrange - Timestamp just past the 10-year boundary
        var justPastTenYears = DateTimeOffset.UtcNow.AddDays(-3651).ToUnixTimeSeconds();
        var port = CreatePort(portName: "Port 10", isUp: false, forwardMode: "native", lastConnectionSeen: justPastTenYears);
        var networks = CreateNetworkList();

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert - Just past boundary should be treated as invalid
        result.Should().BeNull("port just past 10-year boundary should not be flagged");
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
