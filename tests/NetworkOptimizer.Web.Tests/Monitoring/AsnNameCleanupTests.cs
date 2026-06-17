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

    [Fact]
    public void Does_not_strip_geographic_words_generically()
        // The Arelion override is exact-match, not a blanket "Sweden" strip - a real ISP
        // could legitimately be named this way.
        => AsnNameCleanup.Clean("Acme Sweden").Should().Be("Acme Sweden");
}
