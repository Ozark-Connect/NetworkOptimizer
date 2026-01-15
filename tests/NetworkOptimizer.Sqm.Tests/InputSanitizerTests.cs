using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>
/// Tests for InputSanitizer to ensure proper validation and sanitization
/// of user inputs before they're embedded in shell scripts.
/// These tests verify protection against command injection attacks.
/// </summary>
public class InputSanitizerTests
{
    #region ValidatePingHost Tests

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    public void ValidatePingHost_ValidIPv4_ReturnsValid(string ip)
    {
        var (isValid, error) = InputSanitizer.ValidatePingHost(ip);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("fe80::1")]
    public void ValidatePingHost_ValidIPv6_ReturnsValid(string ip)
    {
        var (isValid, error) = InputSanitizer.ValidatePingHost(ip);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("google.com")]
    [InlineData("dns.cloudflare.com")]
    [InlineData("one.one.one.one")]
    [InlineData("test-server.example.com")]
    [InlineData("a.b.c")]
    public void ValidatePingHost_ValidHostname_ReturnsValid(string hostname)
    {
        var (isValid, error) = InputSanitizer.ValidatePingHost(hostname);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidatePingHost_EmptyOrNull_ReturnsInvalid(string? pingHost)
    {
        var (isValid, error) = InputSanitizer.ValidatePingHost(pingHost);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("1.1.1.1; rm -rf /")]
    [InlineData("1.1.1.1\"; nc -l 8080")]
    [InlineData("$(whoami)")]
    [InlineData("`id`")]
    [InlineData("test|cat /etc/passwd")]
    [InlineData("host&whoami")]
    [InlineData("host;id")]
    [InlineData("host\nid")]
    [InlineData("host'id")]
    [InlineData("test$(rm -rf /)")]
    public void ValidatePingHost_CommandInjectionAttempts_ReturnsInvalid(string maliciousInput)
    {
        var (isValid, error) = InputSanitizer.ValidatePingHost(maliciousInput);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("-test.com")]
    [InlineData("test-.com")]
    public void ValidatePingHost_InvalidHostnameFormat_ReturnsInvalid(string hostname)
    {
        var (isValid, error) = InputSanitizer.ValidatePingHost(hostname);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    #endregion

    #region ValidateSpeedtestServerId Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateSpeedtestServerId_EmptyOrNull_ReturnsValid(string? serverId)
    {
        var (isValid, error) = InputSanitizer.ValidateSpeedtestServerId(serverId);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1")]
    [InlineData("9999999999")]
    public void ValidateSpeedtestServerId_ValidNumeric_ReturnsValid(string serverId)
    {
        var (isValid, error) = InputSanitizer.ValidateSpeedtestServerId(serverId);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12345; rm -rf /")]
    [InlineData("123$(whoami)")]
    [InlineData("123`id`")]
    [InlineData("12-34")]
    [InlineData("12.34")]
    public void ValidateSpeedtestServerId_NonNumeric_ReturnsInvalid(string serverId)
    {
        var (isValid, error) = InputSanitizer.ValidateSpeedtestServerId(serverId);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateSpeedtestServerId_TooLong_ReturnsInvalid()
    {
        var (isValid, error) = InputSanitizer.ValidateSpeedtestServerId("12345678901");
        Assert.False(isValid);
        Assert.Contains("too long", error);
    }

    #endregion

    #region SanitizeConnectionName Tests

    [Theory]
    [InlineData("WAN1", "wan1")]
    [InlineData("My Connection", "my-connection")]
    [InlineData("Test (Primary)", "test-primary")]
    [InlineData("Test-Connection", "test-connection")]
    [InlineData("STARLINK", "starlink")]
    public void SanitizeConnectionName_ValidNames_ReturnsSanitized(string input, string expected)
    {
        var result = InputSanitizer.SanitizeConnectionName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeConnectionName_EmptyOrNull_ReturnsWan(string? input)
    {
        var result = InputSanitizer.SanitizeConnectionName(input);
        Assert.Equal("wan", result);
    }

    [Theory]
    [InlineData("test$(whoami)")]
    [InlineData("test`id`")]
    [InlineData("test;rm -rf /")]
    [InlineData("test|cat /etc/passwd")]
    [InlineData("test&whoami")]
    [InlineData("test\"injection")]
    [InlineData("test'injection")]
    [InlineData("test\\injection")]
    public void SanitizeConnectionName_CommandInjectionAttempts_RemovesDangerousChars(string maliciousInput)
    {
        var result = InputSanitizer.SanitizeConnectionName(maliciousInput);
        // Should not contain any shell metacharacters
        Assert.DoesNotContain("$", result);
        Assert.DoesNotContain("`", result);
        Assert.DoesNotContain(";", result);
        Assert.DoesNotContain("|", result);
        Assert.DoesNotContain("&", result);
        Assert.DoesNotContain("\"", result);
        Assert.DoesNotContain("'", result);
        Assert.DoesNotContain("\\", result);
        Assert.DoesNotContain("(", result);
        Assert.DoesNotContain(")", result);
    }

    [Fact]
    public void SanitizeConnectionName_LongName_TruncatesTo32Chars()
    {
        var longName = new string('a', 100);
        var result = InputSanitizer.SanitizeConnectionName(longName);
        Assert.True(result.Length <= 32);
    }

    [Fact]
    public void SanitizeConnectionName_OnlySpecialChars_ReturnsWan()
    {
        var result = InputSanitizer.SanitizeConnectionName("$()`;|&");
        Assert.Equal("wan", result);
    }

    #endregion

    #region ValidateCronSchedule Tests

    [Theory]
    [InlineData("0 6 * * *")]
    [InlineData("30 18 * * *")]
    [InlineData("*/5 * * * *")]
    [InlineData("0 0 * * *")]
    [InlineData("59 23 * * *")]
    public void ValidateCronSchedule_ValidSchedules_ReturnsValid(string schedule)
    {
        var (isValid, error) = InputSanitizer.ValidateCronSchedule(schedule);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0 6")]
    [InlineData("30 18")]
    public void ValidateCronSchedule_MinimalSchedule_ReturnsValid(string schedule)
    {
        var (isValid, error) = InputSanitizer.ValidateCronSchedule(schedule);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateCronSchedule_EmptyOrNull_ReturnsInvalid(string? schedule)
    {
        var (isValid, error) = InputSanitizer.ValidateCronSchedule(schedule);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("60 0 * * *")]  // Invalid minute (60)
    [InlineData("0 24 * * *")]  // Invalid hour (24)
    [InlineData("-1 0 * * *")]  // Negative minute
    [InlineData("abc def")]     // Non-numeric
    public void ValidateCronSchedule_InvalidValues_ReturnsInvalid(string schedule)
    {
        var (isValid, error) = InputSanitizer.ValidateCronSchedule(schedule);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("0 6 * * *; rm -rf /")]
    [InlineData("0 6 $(whoami) * *")]
    [InlineData("0 6 `id` * *")]
    public void ValidateCronSchedule_CommandInjectionAttempts_ReturnsInvalid(string schedule)
    {
        var (isValid, error) = InputSanitizer.ValidateCronSchedule(schedule);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    #endregion

    #region ValidateInterface Tests

    [Theory]
    [InlineData("eth0")]
    [InlineData("ppp0")]
    [InlineData("br0")]
    [InlineData("eth4.832")]
    [InlineData("wlan0")]
    public void ValidateInterface_ValidNames_ReturnsValid(string interfaceName)
    {
        var (isValid, error) = InputSanitizer.ValidateInterface(interfaceName);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateInterface_EmptyOrNull_ReturnsInvalid(string? interfaceName)
    {
        var (isValid, error) = InputSanitizer.ValidateInterface(interfaceName);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("eth0; rm -rf /")]
    [InlineData("eth0$(whoami)")]
    [InlineData("eth0`id`")]
    [InlineData("eth0|cat")]
    [InlineData("eth0&id")]
    public void ValidateInterface_CommandInjectionAttempts_ReturnsInvalid(string interfaceName)
    {
        var (isValid, error) = InputSanitizer.ValidateInterface(interfaceName);
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateInterface_TooLong_ReturnsInvalid()
    {
        var (isValid, error) = InputSanitizer.ValidateInterface("this_is_a_very_long_interface_name");
        Assert.False(isValid);
        Assert.Contains("too long", error);
    }

    #endregion

    #region EscapeForShellDoubleQuote Tests

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("test123", "test123")]
    public void EscapeForShellDoubleQuote_SafeStrings_ReturnsUnchanged(string input, string expected)
    {
        var result = InputSanitizer.EscapeForShellDoubleQuote(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("$HOME", "\\$HOME")]
    [InlineData("`whoami`", "\\`whoami\\`")]
    [InlineData("test\"quote", "test\\\"quote")]
    [InlineData("back\\slash", "back\\\\slash")]
    public void EscapeForShellDoubleQuote_DangerousChars_EscapesThem(string input, string expected)
    {
        var result = InputSanitizer.EscapeForShellDoubleQuote(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EscapeForShellDoubleQuote_EmptyOrNull_ReturnsEmpty(string? input)
    {
        var result = InputSanitizer.EscapeForShellDoubleQuote(input ?? string.Empty);
        Assert.Equal(string.Empty, result);
    }

    #endregion
}
