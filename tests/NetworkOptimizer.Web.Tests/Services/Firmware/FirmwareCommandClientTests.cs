using FluentAssertions;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.Firmware;

public class FirmwareCommandClientTests
{
    [Theory]
    [InlineData("https://fw-download.ubnt.com/data/firmware/abc123.bin", true)]
    [InlineData("http://fw-download.ubnt.com/data/firmware/abc123.bin", true)]
    [InlineData("https://fw-download.ubnt.com/path?query=1&other=2", true)]
    [InlineData("ftp://fw-download.ubnt.com/firmware.bin", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    [InlineData("https://fw-download.ubnt.com/firmware.bin; reboot", false)]
    [InlineData("https://fw-download.ubnt.com/firmware.bin\treboot", false)]
    [InlineData("https://fw-download.ubnt.com/firmware.bin'$(id)", false)]
    [InlineData("https://example.com/f.bin\nwhoami", false)]
    public void IsSafeFirmwareUrl(string url, bool expected)
    {
        FirmwareCommandClient.IsSafeFirmwareUrl(url).Should().Be(expected);
    }
}
