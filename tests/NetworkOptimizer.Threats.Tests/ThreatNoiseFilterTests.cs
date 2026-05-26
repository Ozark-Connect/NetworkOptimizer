using FluentAssertions;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

/// <summary>
/// Verifies ThreatNoiseFilter's new Category, Label, IsSystem fields and that the
/// existing Matches() contract is unchanged by their addition.
/// </summary>
public class ThreatNoiseFilterTests
{
    [Fact]
    public void DefaultCategory_IsNoise()
    {
        var filter = new ThreatNoiseFilter();
        filter.Category.Should().Be(ThreatFilterCategory.Noise);
    }

    [Fact]
    public void DefaultIsSystem_IsFalse()
    {
        var filter = new ThreatNoiseFilter();
        filter.IsSystem.Should().BeFalse();
    }

    [Fact]
    public void Matches_ExactSourceIp_StillWorksWithCategorySet()
    {
        var filter = new ThreatNoiseFilter
        {
            SourceIp = "192.0.2.10",
            Category = ThreatFilterCategory.Infrastructure,
            Label = "self"
        };

        filter.Matches("192.0.2.10", null, null).Should().BeTrue();
        filter.Matches("192.0.2.11", null, null).Should().BeFalse();
    }

    [Fact]
    public void Matches_CidrSourceIp_StillWorks()
    {
        var filter = new ThreatNoiseFilter
        {
            SourceIp = "192.0.2.0/24",
            Category = ThreatFilterCategory.Infrastructure
        };

        filter.Matches("192.0.2.10", null, null).Should().BeTrue();
        filter.Matches("192.0.2.20", null, null).Should().BeTrue();
        filter.Matches("198.51.100.25", null, null).Should().BeFalse();
    }

    [Fact]
    public void Matches_SourceAndDestAndPort_AllRequired()
    {
        var filter = new ThreatNoiseFilter
        {
            SourceIp = "192.0.2.10",
            DestIp = "8.8.8.8",
            DestPort = 53,
            Category = ThreatFilterCategory.Noise
        };

        filter.Matches("192.0.2.10", "8.8.8.8", 53).Should().BeTrue();
        filter.Matches("192.0.2.10", "8.8.8.8", 443).Should().BeFalse();
        filter.Matches("192.0.2.10", "1.1.1.1", 53).Should().BeFalse();
    }
}
