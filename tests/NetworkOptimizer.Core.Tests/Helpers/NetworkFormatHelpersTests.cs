using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class NetworkFormatHelpersTests
{
    [Theory]
    // Industry suffixes (the storage-time cleaner used by discovery and manual target add).
    [InlineData("Cogent Communications, LLC", "Cogent")]
    [InlineData("Level 3 Parent, LLC", "Level 3")]
    [InlineData("Hisense Broadband Technologies Co Ltd", "Hisense")]
    // "Bandwidth" is an industry suffix too, so Zayo's two GeoLite2 forms ("Zayo Bandwidth"
    // and "Zayo Group, LLC") both collapse to the same household name.
    [InlineData("Zayo Bandwidth", "Zayo")]
    [InlineData("Zayo Group, LLC", "Zayo")]
    public void CleanOrgName_strips_industry_and_legal_suffixes(string raw, string expected)
        => NetworkFormatHelpers.CleanOrgName(raw).Should().Be(expected);

    [Fact]
    public void CleanOrgName_keeps_bandwidth_when_it_is_the_whole_brand()
        // "Bandwidth" only strips as a trailing word; it must not erase a standalone brand.
        => NetworkFormatHelpers.CleanOrgName("Bandwidth Inc").Should().Be("Bandwidth");
}
