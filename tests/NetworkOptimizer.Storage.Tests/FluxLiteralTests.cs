using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class FluxLiteralTests
{
    [Theory]
    [InlineData("")]
    [InlineData("network_monitoring")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("eth0.10")]
    [InlineData("custom_oid-1")]
    [InlineData("1.3.6.1.2.1.2.2.1.10")]
    [InlineData("Port 1/0/1")]
    [InlineData("WAN2")]
    [InlineData("cost $5")]
    public void Quote_OrdinaryValue_IsUnchangedInsideQuotes(string value)
    {
        FluxLiteral.Quote(value).Should().Be("\"" + value + "\"");
    }

    [Fact]
    public void Quote_Null_IsEmptyLiteral()
    {
        FluxLiteral.Quote(null).Should().Be("\"\"");
    }

    [Theory]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    [InlineData("a\nb", "\"a\\nb\"")]
    [InlineData("a\rb", "\"a\\rb\"")]
    [InlineData("a\tb", "\"a\\tb\"")]
    [InlineData("a${x}b", "\"a\\${x}b\"")]
    [InlineData("a\u0001b", "\"a\\x01b\"")]
    [InlineData("\\\"", "\"\\\\\\\"\"")]
    public void Quote_SpecialCharacter_IsEscaped(string value, string expected)
    {
        FluxLiteral.Quote(value).Should().Be(expected);
    }

    [Fact]
    public void Quote_InjectionAttempt_StaysOneLiteral()
    {
        var quoted = FluxLiteral.Quote("x\")\nfrom(bucket: \"other-network_monitoring\") |> to(bucket: \"${b}\")\\");

        quoted.Should().StartWith("\"").And.EndWith("\"");
        UnescapedQuotes(quoted).Should().Be(2, "only the opening and closing quotes are unescaped");
        quoted.Should().NotContain("\n");
        System.Text.RegularExpressions.Regex.IsMatch(quoted, @"(?<!\\)\$\{").Should().BeFalse();
        quoted.Should().Contain("\\${");
    }

    /// <summary>Quotes not preceded by an odd run of backslashes.</summary>
    internal static int UnescapedQuotes(string flux)
    {
        var count = 0;
        for (var i = 0; i < flux.Length; i++)
        {
            if (flux[i] != '"') continue;
            var slashes = 0;
            for (var j = i - 1; j >= 0 && flux[j] == '\\'; j--) slashes++;
            if (slashes % 2 == 0) count++;
        }
        return count;
    }
}
