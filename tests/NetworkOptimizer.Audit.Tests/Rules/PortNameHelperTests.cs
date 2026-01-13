using FluentAssertions;
using NetworkOptimizer.Audit.Rules;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class PortNameHelperTests
{
    #region IsDefaultPortName - Standard Port Names

    [Theory]
    [InlineData("Port 1")]
    [InlineData("Port 10")]
    [InlineData("Port 24")]
    [InlineData("Port 48")]
    [InlineData("port 5")]      // Case insensitive
    [InlineData("PORT 8")]      // All caps
    [InlineData("Port1")]       // No space
    [InlineData("Port24")]      // No space
    public void IsDefaultPortName_StandardPortNames_ReturnsTrue(string portName)
    {
        PortNameHelper.IsDefaultPortName(portName).Should().BeTrue(
            $"'{portName}' should be recognized as a default port name");
    }

    #endregion

    #region IsDefaultPortName - SFP Variants

    [Theory]
    [InlineData("SFP 1")]       // Basic SFP
    [InlineData("SFP 2")]
    [InlineData("sfp 1")]       // Case insensitive
    [InlineData("SFP1")]        // No space
    [InlineData("SFP+ 1")]      // SFP+
    [InlineData("SFP+ 2")]
    [InlineData("SFP+1")]       // No space
    [InlineData("sfp+ 1")]      // Case insensitive
    [InlineData("SFP28 1")]     // 25 Gbps SFP28
    [InlineData("SFP28 2")]
    [InlineData("sfp28 1")]     // Case insensitive
    [InlineData("SFP56 1")]     // 50 Gbps SFP56
    [InlineData("SFP56 2")]
    public void IsDefaultPortName_SfpVariants_ReturnsTrue(string portName)
    {
        PortNameHelper.IsDefaultPortName(portName).Should().BeTrue(
            $"'{portName}' should be recognized as a default port name");
    }

    #endregion

    #region IsDefaultPortName - QSFP Variants

    [Theory]
    [InlineData("QSFP 1")]      // Basic QSFP
    [InlineData("QSFP+ 1")]     // QSFP+ (40 Gbps)
    [InlineData("QSFP+1")]      // No space
    [InlineData("qsfp+ 1")]     // Case insensitive
    [InlineData("QSFP28 1")]    // 100 Gbps QSFP28
    [InlineData("QSFP28 2")]
    [InlineData("qsfp28 1")]    // Case insensitive
    [InlineData("QSFP56 1")]    // 200 Gbps QSFP56
    [InlineData("QSFP56 2")]
    public void IsDefaultPortName_QsfpVariants_ReturnsTrue(string portName)
    {
        PortNameHelper.IsDefaultPortName(portName).Should().BeTrue(
            $"'{portName}' should be recognized as a default port name");
    }

    #endregion

    #region IsDefaultPortName - Bare Numbers

    [Theory]
    [InlineData("1")]
    [InlineData("8")]
    [InlineData("24")]
    [InlineData("48")]
    public void IsDefaultPortName_BareNumbers_ReturnsTrue(string portName)
    {
        PortNameHelper.IsDefaultPortName(portName).Should().BeTrue(
            $"'{portName}' (bare number) should be recognized as a default port name");
    }

    #endregion

    #region IsDefaultPortName - Empty/Null

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsDefaultPortName_NullOrEmpty_ReturnsTrue(string? portName)
    {
        PortNameHelper.IsDefaultPortName(portName).Should().BeTrue(
            "null/empty port names should be treated as default");
    }

    #endregion

    #region IsCustomPortName - Actual Custom Names

    [Theory]
    [InlineData("Printer")]
    [InlineData("Camera")]
    [InlineData("Server 1")]
    [InlineData("AP-Lobby")]
    [InlineData("John's PC")]
    [InlineData("Meeting Room Display")]
    [InlineData("NAS Storage")]
    [InlineData("Uplink to Core")]
    [InlineData("PoE+ Camera")]
    [InlineData("Front Desk")]
    public void IsCustomPortName_ActualCustomNames_ReturnsTrue(string portName)
    {
        PortNameHelper.IsCustomPortName(portName).Should().BeTrue(
            $"'{portName}' should be recognized as a custom port name");
    }

    #endregion

    #region IsCustomPortName - Should NOT Match These as Custom

    [Theory]
    [InlineData("Port 1")]
    [InlineData("SFP+ 2")]
    [InlineData("QSFP28 1")]
    [InlineData("8")]
    [InlineData(null)]
    [InlineData("")]
    public void IsCustomPortName_DefaultNames_ReturnsFalse(string? portName)
    {
        PortNameHelper.IsCustomPortName(portName).Should().BeFalse(
            $"'{portName ?? "(null)"}' should NOT be recognized as a custom port name");
    }

    #endregion

    #region Edge Cases

    [Theory]
    [InlineData("  Port 1  ")]  // Leading/trailing whitespace
    [InlineData("  SFP+ 2  ")]
    [InlineData("  8  ")]
    public void IsDefaultPortName_WithWhitespace_ReturnsTrue(string portName)
    {
        PortNameHelper.IsDefaultPortName(portName).Should().BeTrue(
            $"'{portName}' should be recognized as default even with whitespace");
    }

    [Fact]
    public void IsDefaultPortName_PortWithDescription_ReturnsFalse()
    {
        // "Port 1 - Printer" is a custom name, not default
        PortNameHelper.IsDefaultPortName("Port 1 - Printer").Should().BeFalse();
    }

    [Fact]
    public void IsDefaultPortName_SfpWithDescription_ReturnsFalse()
    {
        // "SFP+ 1 Uplink" is a custom name
        PortNameHelper.IsDefaultPortName("SFP+ 1 Uplink").Should().BeFalse();
    }

    #endregion
}
