using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class UrlSafetyTests
{
    [Theory]
    [InlineData("https://fw-download.ubnt.com/data/firmware/abc123.bin", true)]
    [InlineData("http://fw-download.ubnt.com/data/firmware/abc123.bin", true)]
    [InlineData("https://fw-download.ubnt.com/path?query=1&other=2", true)]
    [InlineData("https://speed.example.com:3000", true)]
    [InlineData("http://192.0.2.10:3000/", true)]
    [InlineData("ftp://fw-download.ubnt.com/firmware.bin", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("https://fw-download.ubnt.com/firmware.bin; reboot", false)]
    [InlineData("https://fw-download.ubnt.com/firmware.bin\treboot", false)]
    [InlineData("https://fw-download.ubnt.com/firmware.bin'$(id)", false)]
    [InlineData("https://speed.example.com/x');alert(1);('", false)]
    [InlineData("https://speed.example.com/x\"onerror=alert(1)", false)]
    [InlineData("https://example.com/f.bin\nwhoami", false)]
    public void IsSafeHttpUrl(string? url, bool expected)
    {
        UrlSafety.IsSafeHttpUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("192.0.2.10", true)]
    [InlineData("192.0.2.10:3000", true)]
    [InlineData("speedtest.example.com", true)]
    [InlineData("speedtest.example.com:3000", true)]
    [InlineData("speedtest", true)]
    [InlineData("2001:db8::10", true)]
    [InlineData("[2001:db8::10]:3000", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("speed test.example.com", false)]
    [InlineData("example.com/path", false)]
    [InlineData("host'); alert(1); ('", false)]
    [InlineData("-bad.example.com", false)]
    [InlineData("example.com:99999x", false)]
    public void IsSafeHost(string? host, bool expected)
    {
        UrlSafety.IsSafeHost(host).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://speed.example.com", true)]
    [InlineData("192.0.2.10:3000", true)]
    [InlineData("https://speed.example.com/x');alert(1);('", false)]
    [InlineData("host'); alert(1); ('", false)]
    public void IsSafeHostOrHttpUrl(string? value, bool expected)
    {
        UrlSafety.IsSafeHostOrHttpUrl(value).Should().Be(expected);
    }
}
