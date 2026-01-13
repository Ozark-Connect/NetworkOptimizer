using FluentAssertions;
using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class RadioFormatHelperTests
{
    #region FormatBand Tests

    [Theory]
    [InlineData("ng", "2.4 GHz")]
    [InlineData("na", "5 GHz")]
    [InlineData("6e", "6 GHz")]
    public void FormatBand_KnownBands_ReturnsHumanReadable(string radio, string expected)
    {
        // Act
        var result = RadioFormatHelper.FormatBand(radio);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("NG", "2.4 GHz")]
    [InlineData("NA", "5 GHz")]
    [InlineData("6E", "6 GHz")]
    [InlineData("Ng", "2.4 GHz")]
    public void FormatBand_CaseInsensitive(string radio, string expected)
    {
        // Act
        var result = RadioFormatHelper.FormatBand(radio);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("something")]
    [InlineData("xyz")]
    public void FormatBand_UnknownBands_ReturnsOriginal(string radio)
    {
        // Act
        var result = RadioFormatHelper.FormatBand(radio);

        // Assert
        result.Should().Be(radio);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FormatBand_NullOrEmpty_ReturnsEmpty(string? radio)
    {
        // Act
        var result = RadioFormatHelper.FormatBand(radio);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region FormatProtocol Tests

    [Theory]
    [InlineData("a", null, "Wi-Fi 1/2 (a)")]
    [InlineData("b", null, "Wi-Fi 1 (b)")]
    [InlineData("g", null, "Wi-Fi 3 (g)")]
    [InlineData("n", null, "Wi-Fi 4 (n)")]
    [InlineData("ac", null, "Wi-Fi 5 (ac)")]
    [InlineData("ax", null, "Wi-Fi 6 (ax)")]
    [InlineData("be", null, "Wi-Fi 7 (be)")]
    public void FormatProtocol_KnownProtocols_ReturnsWiFiGeneration(string proto, string? radio, string expected)
    {
        // Act
        var result = RadioFormatHelper.FormatProtocol(proto, radio);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("A", "Wi-Fi 1/2 (a)")]
    [InlineData("B", "Wi-Fi 1 (b)")]
    [InlineData("G", "Wi-Fi 3 (g)")]
    [InlineData("N", "Wi-Fi 4 (n)")]
    [InlineData("AC", "Wi-Fi 5 (ac)")]
    [InlineData("AX", "Wi-Fi 6 (ax)")]
    [InlineData("BE", "Wi-Fi 7 (be)")]
    public void FormatProtocol_CaseInsensitive(string proto, string expected)
    {
        // Act
        var result = RadioFormatHelper.FormatProtocol(proto);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void FormatProtocol_AxWith6GHz_ReturnsWiFi6E()
    {
        // Act
        var result = RadioFormatHelper.FormatProtocol("ax", "6e");

        // Assert
        result.Should().Be("Wi-Fi 6E (ax)");
    }

    [Fact]
    public void FormatProtocol_AxWith5GHz_ReturnsWiFi6()
    {
        // Act
        var result = RadioFormatHelper.FormatProtocol("ax", "na");

        // Assert
        result.Should().Be("Wi-Fi 6 (ax)");
    }

    [Fact]
    public void FormatProtocol_AxWith24GHz_ReturnsWiFi6()
    {
        // Act
        var result = RadioFormatHelper.FormatProtocol("ax", "ng");

        // Assert
        result.Should().Be("Wi-Fi 6 (ax)");
    }

    [Theory]
    [InlineData("ax", "6E", "Wi-Fi 6E (ax)")]
    [InlineData("AX", "6e", "Wi-Fi 6E (ax)")]
    [InlineData("AX", "6E", "Wi-Fi 6E (ax)")]
    public void FormatProtocol_AxWith6E_CaseInsensitive(string proto, string radio, string expected)
    {
        // Act
        var result = RadioFormatHelper.FormatProtocol(proto, radio);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("unknown")]
    public void FormatProtocol_UnknownProtocols_ReturnsWiFiWithOriginal(string proto)
    {
        // Act
        var result = RadioFormatHelper.FormatProtocol(proto);

        // Assert
        result.Should().Be($"Wi-Fi ({proto})");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FormatProtocol_NullOrEmpty_ReturnsEmpty(string? proto)
    {
        // Act
        var result = RadioFormatHelper.FormatProtocol(proto);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region FormatProtocolSuffix Tests

    [Theory]
    [InlineData("a", null, "1/2 (a)")]
    [InlineData("b", null, "1 (b)")]
    [InlineData("g", null, "3 (g)")]
    [InlineData("n", null, "4 (n)")]
    [InlineData("ac", null, "5 (ac)")]
    [InlineData("ax", null, "6 (ax)")]
    [InlineData("be", null, "7 (be)")]
    public void FormatProtocolSuffix_KnownProtocols_ReturnsSuffix(string proto, string? radio, string expected)
    {
        // Act
        var result = RadioFormatHelper.FormatProtocolSuffix(proto, radio);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void FormatProtocolSuffix_AxWith6GHz_Returns6ESuffix()
    {
        // Act
        var result = RadioFormatHelper.FormatProtocolSuffix("ax", "6e");

        // Assert
        result.Should().Be("6E (ax)");
    }

    [Fact]
    public void FormatProtocolSuffix_AxWithout6GHz_Returns6Suffix()
    {
        // Act
        var result = RadioFormatHelper.FormatProtocolSuffix("ax", "na");

        // Assert
        result.Should().Be("6 (ax)");
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("unknown")]
    public void FormatProtocolSuffix_UnknownProtocols_ReturnsParenthesized(string proto)
    {
        // Act
        var result = RadioFormatHelper.FormatProtocolSuffix(proto);

        // Assert
        result.Should().Be($"({proto})");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FormatProtocolSuffix_NullOrEmpty_ReturnsEmpty(string? proto)
    {
        // Act
        var result = RadioFormatHelper.FormatProtocolSuffix(proto);

        // Assert
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("A", "1/2 (a)")]
    [InlineData("B", "1 (b)")]
    [InlineData("AC", "5 (ac)")]
    [InlineData("AX", "6 (ax)")]
    [InlineData("BE", "7 (be)")]
    public void FormatProtocolSuffix_CaseInsensitive(string proto, string expected)
    {
        // Act
        var result = RadioFormatHelper.FormatProtocolSuffix(proto);

        // Assert
        result.Should().Be(expected);
    }

    #endregion
}
