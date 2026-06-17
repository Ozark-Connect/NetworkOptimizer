using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class AsnNameCleanupTests
{
    [Theory]
    [InlineData("Cloudflare, Inc.", "Cloudflare")]
    [InlineData("Akamai International B.V.", "Akamai International")]
    [InlineData("Arelion Sweden AB", "Arelion")]
    [InlineData("Arelion Sweden", "Arelion")]
    public void Strips_corporate_suffixes_and_applies_brand_overrides(string raw, string expected)
        => AsnNameCleanup.Clean(raw).Should().Be(expected);

    [Theory]
    // Sparkle (AS6762 and regional subsidiaries) appears under many legal-entity names; a
    // whole-word brand token collapses them all to the household name.
    [InlineData("TELECOM ITALIA SPARKLE S.p.A.", "Sparkle")]
    [InlineData("Telecom Italia Sparkle Singapore Pte Ltd", "Sparkle")]
    [InlineData("TI Sparkle Turkey Telekomunukasyon A.S", "Sparkle")]
    [InlineData("TTi Sparkle Greece SA", "Sparkle")]
    public void Collapses_brand_token_across_regional_entities(string raw, string expected)
        => AsnNameCleanup.Clean(raw).Should().Be(expected);

    [Fact]
    public void Brand_token_requires_a_whole_word_so_it_does_not_clobber_lookalikes()
        // "Sparklight" (Cable One) contains "Sparkl" but not the whole word "Sparkle".
        => AsnNameCleanup.Clean("Sparklight").Should().Be("Sparklight");

    [Fact]
    public void Does_not_strip_geographic_words_generically()
        // The Arelion override is exact-match, not a blanket "Sweden" strip - a real ISP
        // could legitimately be named this way.
        => AsnNameCleanup.Clean("Acme Sweden").Should().Be("Acme Sweden");
}
