using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class StringUtilitiesTests
{
    [Fact]
    public void SplitLinks_PlainText_IsOneTextSegment()
    {
        StringUtilities.SplitLinks("No links here.").Should().Equal(("No links here.", false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SplitLinks_Empty_ReturnsNoSegments(string? text)
    {
        StringUtilities.SplitLinks(text).Should().BeEmpty();
    }

    [Fact]
    public void SplitLinks_LeavesSentencePunctuationAndUnopenedParenOutOfUrls()
    {
        var text = "See the README: https://example.com/README.md#reverse-proxy (more help: https://example.com/issues/1154).";

        StringUtilities.SplitLinks(text).Should().Equal(
            ("See the README: ", false),
            ("https://example.com/README.md#reverse-proxy", true),
            (" (more help: ", false),
            ("https://example.com/issues/1154", true),
            (").", false));
    }

    [Fact]
    public void SplitLinks_KeepsParenTheUrlOpened()
    {
        StringUtilities.SplitLinks("Read https://example.com/wiki/Foo_(bar).").Should().Equal(
            ("Read ", false),
            ("https://example.com/wiki/Foo_(bar)", true),
            (".", false));
    }

    [Fact]
    public void SplitLinks_UrlAtStartAndEnd()
    {
        StringUtilities.SplitLinks("http://192.0.2.10:8042/alerts").Should().Equal(("http://192.0.2.10:8042/alerts", true));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("https://.")]
    public void SplitLinks_NonHttpOrEmptyUrl_StaysText(string text)
    {
        StringUtilities.SplitLinks(text).Should().Equal((text, false));
    }
}
