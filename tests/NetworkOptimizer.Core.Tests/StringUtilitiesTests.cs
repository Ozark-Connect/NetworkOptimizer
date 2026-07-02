using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests;

public class StringUtilitiesTests
{
    [Theory]
    [InlineData("Acme Corp", "acme-corp")]
    [InlineData("Lake House", "lake-house")]
    [InlineData("  Main   Site  ", "main-site")]
    [InlineData("Café São Paulo", "cafe-sao-paulo")]
    [InlineData("Site #2 (backup)", "site-2-backup")]
    [InlineData("UPPER_case_name", "upper-case-name")]
    [InlineData("already-a-slug", "already-a-slug")]
    [InlineData("42", "42")]
    public void ToSlug_ProducesKebabCase(string input, string expected)
    {
        StringUtilities.ToSlug(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("!!!")]
    [InlineData("日本語")]
    public void ToSlug_FallsBackToSite_WhenNothingUsable(string? input)
    {
        StringUtilities.ToSlug(input).Should().Be("site");
    }

    [Fact]
    public void ToSlug_TruncatesToMaxLength_WithoutTrailingHyphen()
    {
        var slug = StringUtilities.ToSlug("aaaa bbbb cccc dddd", 9);
        slug.Should().Be("aaaa-bbbb");
        slug.Length.Should().BeLessThanOrEqualTo(9);
    }
}
