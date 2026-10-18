using FluentAssertions;
using NetworkOptimizer.Threats.Waf;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class WafExclusionTests
{
    [Fact]
    public void HostAndPath_ChainsTheTwoConditions()
    {
        var rule = WafExclusion.ForRule(942100, "app.example.com", "/api/search");

        rule.Should().MatchRegex(
            """^SecRule REQUEST_HEADERS:Host "@streq app\.example\.com" "id:\d+,phase:1,pass,nolog,chain"\n    SecRule REQUEST_URI "@beginsWith /api/search" "ctl:ruleRemoveById=942100"$""");
    }

    [Fact]
    public void Id_IsStableAndInTheUserRange()
    {
        var a = WafExclusion.ForRule(942100, "app.example.com", "/x");
        var b = WafExclusion.ForRule(942100, "app.example.com", "/x");
        a.Should().Be(b);

        var id = int.Parse(System.Text.RegularExpressions.Regex.Match(a, @"id:(\d+)").Groups[1].Value);
        id.Should().BeInRange(10000, 99999);
    }

    [Fact]
    public void QuotesAndWhitespace_CannotBreakOutOfTheString()
    {
        var rule = WafExclusion.ForRule(1, "evil\" host", "/a\\b \"c");

        rule.Should().Contain("@streq evilhost").And.Contain("@beginsWith /abc");
    }

    [Theory]
    [InlineData("app.example.com", null, "SecRule REQUEST_HEADERS:Host \"@streq app.example.com\"")]
    [InlineData(null, "/login", "SecRule REQUEST_URI \"@beginsWith /login\"")]
    [InlineData(null, null, "SecRuleRemoveById 920350")]
    public void MissingParts_Widen(string? host, string? path, string start) =>
        WafExclusion.ForRule(920350, host, path).Should().StartWith(start);

    [Theory]
    [InlineData("POST /api/login", "/api/login")]
    [InlineData("/bare", "/bare")]
    [InlineData(null, null)]
    public void PathOf_TakesThePathFromTheStoredRequest(string? service, string? expected) =>
        WafExclusion.PathOf(service).Should().Be(expected);
}
