using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Models;

/// <summary>
/// Tests for NetworkPurpose enum extension methods
/// </summary>
public class NetworkPurposeExtensionsTests
{
    [Theory]
    [InlineData(NetworkPurpose.Corporate, "Corporate")]
    [InlineData(NetworkPurpose.Home, "Home")]
    [InlineData(NetworkPurpose.IoT, "IoT")]
    [InlineData(NetworkPurpose.Security, "Security")]
    [InlineData(NetworkPurpose.Guest, "Guest")]
    [InlineData(NetworkPurpose.Management, "Management")]
    [InlineData(NetworkPurpose.Printer, "Printer")]
    [InlineData(NetworkPurpose.Unknown, "Unclassified")]
    public void ToDisplayString_ReturnsExpectedValue(NetworkPurpose purpose, string expected)
    {
        purpose.ToDisplayString().Should().Be(expected);
    }

    [Fact]
    public void ToDisplayString_UnknownEnumValue_ReturnsToString()
    {
        // Test the default case - cast an invalid int to simulate an unknown enum value
        var unknownPurpose = (NetworkPurpose)999;
        unknownPurpose.ToDisplayString().Should().Be("999");
    }

    [Fact]
    public void NetworkInfo_IsNative_ReturnsTrue_WhenVlanId1()
    {
        var network = new NetworkInfo { Id = "test", Name = "Default", VlanId = 1 };
        network.IsNative.Should().BeTrue();
    }

    [Fact]
    public void NetworkInfo_IsNative_ReturnsFalse_WhenVlanIdNot1()
    {
        var network = new NetworkInfo { Id = "test", Name = "IoT", VlanId = 40 };
        network.IsNative.Should().BeFalse();
    }
}
