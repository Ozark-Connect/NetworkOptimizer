using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class CloudflareIpRangesTests
{
    #region IsCloudflareOnly Tests

    [Fact]
    public void IsCloudflareOnly_AllCloudflareIPv4Ranges_ReturnsTrue()
    {
        CloudflareIpRanges.IsCloudflareOnly(CloudflareIpRanges.IPv4Ranges).Should().BeTrue();
    }

    [Fact]
    public void IsCloudflareOnly_AllCloudflareRanges_ReturnsTrue()
    {
        CloudflareIpRanges.IsCloudflareOnly(CloudflareIpRanges.AllRanges).Should().BeTrue();
    }

    [Fact]
    public void IsCloudflareOnly_SubsetOfCloudflareRanges_ReturnsTrue()
    {
        var subset = new[] { "173.245.48.0/20", "104.16.0.0/13" };
        CloudflareIpRanges.IsCloudflareOnly(subset).Should().BeTrue();
    }

    [Fact]
    public void IsCloudflareOnly_MixedCloudflareAndOther_ReturnsFalse()
    {
        var mixed = new[] { "173.245.48.0/20", "203.0.113.0/24" };
        CloudflareIpRanges.IsCloudflareOnly(mixed).Should().BeFalse();
    }

    [Fact]
    public void IsCloudflareOnly_OnlyNonCloudflare_ReturnsFalse()
    {
        var other = new[] { "203.0.113.0/24", "198.51.100.0/24" };
        CloudflareIpRanges.IsCloudflareOnly(other).Should().BeFalse();
    }

    [Fact]
    public void IsCloudflareOnly_EmptyList_ReturnsFalse()
    {
        CloudflareIpRanges.IsCloudflareOnly(Array.Empty<string>()).Should().BeFalse();
    }

    [Fact]
    public void IsCloudflareOnly_Null_ReturnsFalse()
    {
        CloudflareIpRanges.IsCloudflareOnly(null).Should().BeFalse();
    }

    [Fact]
    public void IsCloudflareOnly_SingleCloudflareRange_ReturnsTrue()
    {
        CloudflareIpRanges.IsCloudflareOnly(["173.245.48.0/20"]).Should().BeTrue();
    }

    [Fact]
    public void IsCloudflareOnly_IPv6CloudflareRanges_ReturnsTrue()
    {
        var ipv6Only = new[] { "2400:cb00::/32", "2606:4700::/32" };
        CloudflareIpRanges.IsCloudflareOnly(ipv6Only).Should().BeTrue();
    }

    [Fact]
    public void IsCloudflareOnly_SingleIpInCloudflareRange_ReturnsTrue()
    {
        // 104.16.0.1 is within 104.16.0.0/13
        CloudflareIpRanges.IsCloudflareOnly(["104.16.0.1"]).Should().BeTrue();
    }

    [Fact]
    public void IsCloudflareOnly_SingleIpNotInCloudflare_ReturnsFalse()
    {
        CloudflareIpRanges.IsCloudflareOnly(["8.8.8.8"]).Should().BeFalse();
    }

    [Fact]
    public void IsCloudflareOnly_SubnetWithinCloudflareRange_ReturnsTrue()
    {
        // 104.16.0.0/16 is within 104.16.0.0/13
        CloudflareIpRanges.IsCloudflareOnly(["104.16.0.0/16"]).Should().BeTrue();
    }

    [Fact]
    public void IsCloudflareOnly_SubnetLargerThanCloudflareRange_ReturnsFalse()
    {
        // 104.0.0.0/8 is larger than 104.16.0.0/13 - not fully covered
        CloudflareIpRanges.IsCloudflareOnly(["104.0.0.0/8"]).Should().BeFalse();
    }

    #endregion

    #region ContainsCloudflareRange Tests

    [Fact]
    public void ContainsCloudflareRange_MixedList_ReturnsTrue()
    {
        var mixed = new[] { "203.0.113.0/24", "173.245.48.0/20" };
        CloudflareIpRanges.ContainsCloudflareRange(mixed).Should().BeTrue();
    }

    [Fact]
    public void ContainsCloudflareRange_NoCloudflare_ReturnsFalse()
    {
        var other = new[] { "203.0.113.0/24", "198.51.100.0/24" };
        CloudflareIpRanges.ContainsCloudflareRange(other).Should().BeFalse();
    }

    [Fact]
    public void ContainsCloudflareRange_Null_ReturnsFalse()
    {
        CloudflareIpRanges.ContainsCloudflareRange(null).Should().BeFalse();
    }

    #endregion

    #region Range Constants Validation

    [Fact]
    public void IPv4Ranges_Contains15Entries()
    {
        CloudflareIpRanges.IPv4Ranges.Should().HaveCount(15);
    }

    [Fact]
    public void IPv6Ranges_Contains7Entries()
    {
        CloudflareIpRanges.IPv6Ranges.Should().HaveCount(7);
    }

    [Fact]
    public void AllRanges_ContainsBothIPv4AndIPv6()
    {
        CloudflareIpRanges.AllRanges.Should().HaveCount(22);
        CloudflareIpRanges.AllRanges.Should().Contain(CloudflareIpRanges.IPv4Ranges);
        CloudflareIpRanges.AllRanges.Should().Contain(CloudflareIpRanges.IPv6Ranges);
    }

    #endregion
}
