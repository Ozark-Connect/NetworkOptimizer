using FluentAssertions;
using NetworkOptimizer.Threats.Waf;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class WafClientUrlTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8044")]
    [InlineData("http://127.10.0.1:8044")]
    [InlineData("http://[::1]:8044")]
    [InlineData("http://localhost:8044")]
    [InlineData("http://LOCALHOST:8044/")]
    [InlineData("https://192.0.2.10:8044")]
    [InlineData("https://waf.example.com")]
    public void UrlProblem_AllowsHttpsAnywhereAndHttpOnLoopback(string url)
    {
        WafClient.UrlProblem(url).Should().BeNull();
    }

    [Theory]
    [InlineData("http://192.0.2.10:8044")]
    [InlineData("http://[2001:db8::1]:8044")]
    [InlineData("http://waf.example.com")]
    [InlineData("http://localhost.example.com:8044")]
    public void UrlProblem_RejectsHttpOffLoopback(string url)
    {
        WafClient.UrlProblem(url).Should().Contain("https://");
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:8044")]
    [InlineData("ftp://127.0.0.1")]
    public void UrlProblem_RejectsNonHttpUrls(string url)
    {
        WafClient.UrlProblem(url).Should().Be("The WAF URL must be an http:// or https:// address.");
    }
}
