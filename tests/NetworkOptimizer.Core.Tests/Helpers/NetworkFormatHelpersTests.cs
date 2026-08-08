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
    // "L.C." / "L.C" / "LC" legal forms (e.g. XMission registers as "XMission, L.C.").
    [InlineData("XMission, L.C.", "XMission")]
    [InlineData("XMission, L.C", "XMission")]
    [InlineData("XMission LC", "XMission")]
    // Dotted legal forms must strip even though the input's trailing '.' is trimmed first
    // (OrgSuffixes entries are stored dotless so they still match the trimmed tail).
    [InlineData("Orange S.A.", "Orange")]
    [InlineData("KPN B.V.", "KPN")]
    [InlineData("Ziggo N.V.", "Ziggo")]
    [InlineData("Hurricane Electric, L.P.", "Hurricane Electric")]
    [InlineData("Hurricane Electric LP", "Hurricane Electric")]
    public void CleanOrgName_strips_industry_and_legal_suffixes(string raw, string expected)
        => NetworkFormatHelpers.CleanOrgName(raw).Should().Be(expected);

    [Fact]
    public void CleanOrgName_keeps_bandwidth_when_it_is_the_whole_brand()
        // "Bandwidth" only strips as a trailing word; it must not erase a standalone brand.
        => NetworkFormatHelpers.CleanOrgName("Bandwidth Inc").Should().Be("Bandwidth");

    [Theory]
    // The alias pass runs last, on whatever the suffix strippers leave behind.
    [InlineData("Space Exploration Technologies Corporation", "SpaceX")]
    [InlineData("Space Exploration Technologies", "SpaceX")]
    [InlineData("Space Exploration", "SpaceX")]
    public void CleanOrgName_aliases_the_stripped_name_to_the_known_one(string raw, string expected)
        => NetworkFormatHelpers.CleanOrgName(raw).Should().Be(expected);

    [Fact]
    public void CleanOrgName_alias_matches_the_whole_name_only()
        // "Partners" is not a suffix this strips, so the name never becomes the alias key - and an
        // unrelated firm that merely starts with those words keeps its own name.
        => NetworkFormatHelpers.CleanOrgName("Space Exploration Partners")
            .Should().Be("Space Exploration Partners");
}
