using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class AuditRuleBaseTests
{
    private readonly TestableAuditRule _rule;

    public AuditRuleBaseTests()
    {
        _rule = new TestableAuditRule();
    }

    #region GetBestDeviceName Priority Tests

    [Fact]
    public void CreateIssue_WithConnectedClientName_UsesClientName()
    {
        // Arrange
        var port = CreatePort(
            portName: "Port 1",
            switchName: "Test Switch",
            connectedClient: new UniFiClientResponse { Name = "Office Laptop" });

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Office Laptop on Test Switch");
    }

    [Fact]
    public void CreateIssue_WithConnectedClientHostname_UsesHostname()
    {
        // Arrange
        var port = CreatePort(
            portName: "Port 1",
            switchName: "Test Switch",
            connectedClient: new UniFiClientResponse { Hostname = "desktop-abc123" });

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("desktop-abc123 on Test Switch");
    }

    [Fact]
    public void CreateIssue_WithConnectedClientNameAndHostname_PrefersName()
    {
        // Arrange
        var port = CreatePort(
            portName: "Port 1",
            switchName: "Test Switch",
            connectedClient: new UniFiClientResponse
            {
                Name = "Friendly Name",
                Hostname = "hostname-123"
            });

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Friendly Name on Test Switch");
    }

    [Fact]
    public void CreateIssue_WithHistoricalClientDisplayName_UsesDisplayName()
    {
        // Arrange
        var port = CreatePort(
            portName: "Port 1",
            switchName: "Test Switch",
            historicalClient: new UniFiClientDetailResponse { DisplayName = "Camera Front" });

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Camera Front on Test Switch");
    }

    [Fact]
    public void CreateIssue_WithHistoricalClientName_UsesName()
    {
        // Arrange
        var port = CreatePort(
            portName: "Port 1",
            switchName: "Test Switch",
            historicalClient: new UniFiClientDetailResponse { Name = "Historical Device" });

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Historical Device on Test Switch");
    }

    [Fact]
    public void CreateIssue_WithHistoricalClientHostname_UsesHostname()
    {
        // Arrange
        var port = CreatePort(
            portName: "Port 1",
            switchName: "Test Switch",
            historicalClient: new UniFiClientDetailResponse { Hostname = "hist-host" });

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("hist-host on Test Switch");
    }

    [Fact]
    public void CreateIssue_ConnectedClientTakesPriorityOverHistorical()
    {
        // Arrange
        var port = CreatePort(
            portName: "Custom Name",
            switchName: "Test Switch",
            connectedClient: new UniFiClientResponse { Name = "Connected Device" },
            historicalClient: new UniFiClientDetailResponse { DisplayName = "Historical Device" });

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Connected Device on Test Switch");
    }

    [Fact]
    public void CreateIssue_HistoricalClientTakesPriorityOverPortName()
    {
        // Arrange
        var port = CreatePort(
            portName: "Camera Porch",
            switchName: "Test Switch",
            historicalClient: new UniFiClientDetailResponse { DisplayName = "Historical Camera" });

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Historical Camera on Test Switch");
    }

    #endregion

    #region Custom Port Name Detection Tests

    [Fact]
    public void CreateIssue_WithCustomPortName_UsesPortName()
    {
        // Arrange - no client info, just a custom port name
        var port = CreatePort(
            portName: "Camera Garage",
            portIndex: 5,
            switchName: "POE Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Camera Garage on POE Switch");
    }

    [Fact]
    public void CreateIssue_WithBareNumber_FallsBackToPortIndex()
    {
        // Arrange
        var port = CreatePort(
            portName: "8",
            portIndex: 8,
            switchName: "Main Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Port 8 on Main Switch");
    }

    [Theory]
    [InlineData("Port 1")]
    [InlineData("Port 8")]
    [InlineData("Port1")]
    [InlineData("port 5")]
    [InlineData("PORT 12")]
    public void CreateIssue_WithDefaultPortPattern_FallsBackToPortIndex(string portName)
    {
        // Arrange
        var port = CreatePort(
            portName: portName,
            portIndex: 3,
            switchName: "Test Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Port 3 on Test Switch");
    }

    [Theory]
    [InlineData("SFP+ 1")]
    [InlineData("SFP+ 2")]
    [InlineData("sfp+ 1")]
    [InlineData("SFP+1")]
    public void CreateIssue_WithSfpPlusPattern_FallsBackToPortIndex(string portName)
    {
        // Arrange
        var port = CreatePort(
            portName: portName,
            portIndex: 25,
            switchName: "Aggregation Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Port 25 on Aggregation Switch");
    }

    [Theory]
    [InlineData("SFP28 1")]
    [InlineData("SFP28 2")]
    [InlineData("sfp28 1")]
    [InlineData("SFP281")]
    public void CreateIssue_WithSfp28Pattern_FallsBackToPortIndex(string portName)
    {
        // Arrange
        var port = CreatePort(
            portName: portName,
            portIndex: 49,
            switchName: "Core Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Port 49 on Core Switch");
    }

    [Theory]
    [InlineData("QSFP28 1")]
    [InlineData("QSFP28 2")]
    [InlineData("qsfp28 1")]
    [InlineData("QSFP281")]
    public void CreateIssue_WithQsfp28Pattern_FallsBackToPortIndex(string portName)
    {
        // Arrange
        var port = CreatePort(
            portName: portName,
            portIndex: 51,
            switchName: "Core Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Port 51 on Core Switch");
    }

    [Theory]
    [InlineData("QSFP+ 1")]
    [InlineData("QSFP+ 2")]
    [InlineData("qsfp+ 1")]
    public void CreateIssue_WithQsfpPlusPattern_FallsBackToPortIndex(string portName)
    {
        // Arrange
        var port = CreatePort(
            portName: portName,
            portIndex: 53,
            switchName: "Core Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Port 53 on Core Switch");
    }

    [Theory]
    [InlineData("SFP 1")] // SFP without + or 28
    [InlineData("SFP 2")]
    public void CreateIssue_WithBaseSfpPattern_FallsBackToPortIndex(string portName)
    {
        // Arrange
        var port = CreatePort(
            portName: portName,
            portIndex: 9,
            switchName: "Edge Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Port 9 on Edge Switch");
    }

    #endregion

    #region Custom Names That Should Be Used

    [Theory]
    [InlineData("Camera Front Door")]
    [InlineData("NVR")]
    [InlineData("Access Point Lobby")]
    [InlineData("Server Rack PDU")]
    [InlineData("Printer-Office")]
    [InlineData("Gaming PC")]
    public void CreateIssue_WithDescriptivePortName_UsesPortName(string portName)
    {
        // Arrange
        var port = CreatePort(
            portName: portName,
            portIndex: 7,
            switchName: "Test Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be($"{portName} on Test Switch");
    }

    [Fact]
    public void CreateIssue_WithSfpInDescriptiveName_UsesPortName()
    {
        // "SFP Uplink to Core" is descriptive, not a default pattern
        var port = CreatePort(
            portName: "SFP Uplink to Core",
            portIndex: 25,
            switchName: "Edge Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("SFP Uplink to Core on Edge Switch");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void CreateIssue_WithEmptyPortName_FallsBackToPortIndex()
    {
        // Arrange
        var port = CreatePort(
            portName: "",
            portIndex: 4,
            switchName: "Test Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Port 4 on Test Switch");
    }

    [Fact]
    public void CreateIssue_WithWhitespacePortName_FallsBackToPortIndex()
    {
        // Arrange
        var port = CreatePort(
            portName: "   ",
            portIndex: 4,
            switchName: "Test Switch");

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Port 4 on Test Switch");
    }

    [Fact]
    public void CreateIssue_WithEmptyClientName_TriesNextPriority()
    {
        // Arrange - client with empty name should fall through to historical
        var port = CreatePort(
            portName: "Custom Port",
            switchName: "Test Switch",
            connectedClient: new UniFiClientResponse { Name = "", Hostname = "" },
            historicalClient: new UniFiClientDetailResponse { DisplayName = "Historical Name" });

        // Act
        var issue = _rule.TestCreateIssue("Test message", port);

        // Assert
        issue.DeviceName.Should().Be("Historical Name on Test Switch");
    }

    #endregion

    #region Helper Methods

    private static PortInfo CreatePort(
        string portName = "Port 1",
        int portIndex = 1,
        string switchName = "Test Switch",
        UniFiClientResponse? connectedClient = null,
        UniFiClientDetailResponse? historicalClient = null)
    {
        var switchInfo = new SwitchInfo { Name = switchName };

        return new PortInfo
        {
            PortIndex = portIndex,
            Name = portName,
            IsUp = true,
            ForwardMode = "native",
            Switch = switchInfo,
            ConnectedClient = connectedClient,
            HistoricalClient = historicalClient
        };
    }

    #endregion

    #region Test Helper Classes

    /// <summary>
    /// Concrete implementation of AuditRuleBase for testing protected methods
    /// </summary>
    private class TestableAuditRule : AuditRuleBase
    {
        public override string RuleId => "TEST-001";
        public override string RuleName => "Test Rule";
        public override string Description => "Test rule for testing base class functionality";
        public override AuditSeverity Severity => AuditSeverity.Recommended;

        public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null)
        {
            return CreateIssue("Test issue", port);
        }

        /// <summary>
        /// Expose CreateIssue for testing
        /// </summary>
        public AuditIssue TestCreateIssue(string message, PortInfo port, Dictionary<string, object>? metadata = null)
        {
            return CreateIssue(message, port, metadata);
        }
    }

    #endregion
}
