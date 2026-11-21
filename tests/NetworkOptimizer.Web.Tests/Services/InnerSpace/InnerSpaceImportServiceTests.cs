using System.Text;
using FluentAssertions;
using NetworkOptimizer.Web.Services.InnerSpace;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.InnerSpace;

public class InnerSpaceImportServiceTests
{
    [Theory]
    [InlineData("/proxy/innerspace/integration/v1/assets/abc/scan.zip", "proxy/innerspace/integration/v1/assets/abc/scan.zip")]
    [InlineData("/proxy/innerspace/abc/plan.png", "proxy/innerspace/abc/plan.png")]
    [InlineData("/proxy/network/api/s/default/stat/device", null)]
    [InlineData("/api/users/self", null)]
    [InlineData("https://example.com/proxy/innerspace/x.png", null)]
    [InlineData("/proxy/innerspace/../../api/auth/login", null)]
    [InlineData("/proxy/innerspace/%2E%2E/%2e%2e/api/auth/login", null)]
    [InlineData("/proxy/innerspace\\..\\api", null)]
    [InlineData(null, null)]
    public void ConsolePath_OnlyFetchesUnderInnerSpacesProxy(string? url, string? expected) =>
        InnerSpaceImportService.ConsolePath(url).Should().Be(expected);

    [Fact]
    public void SniffImageType_ReadsTheBytesNotTheName()
    {
        InnerSpaceImportService.SniffImageType(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }).Should().Be("image/png");
        InnerSpaceImportService.SniffImageType(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }).Should().Be("image/jpeg");
        InnerSpaceImportService.SniffImageType(Encoding.UTF8.GetBytes("<svg version=\"1.1\"></svg>")).Should().Be("image/svg+xml");
        InnerSpaceImportService.SniffImageType(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>\n<svg></svg>")).Should().Be("image/svg+xml");
        InnerSpaceImportService.SniffImageType(Encoding.UTF8.GetBytes("<html>")).Should().Be("application/octet-stream");
    }
}
