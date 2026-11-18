using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

/// <summary><see cref="NetworkUtilities.IsTailscaleAddress(string?)"/>: Tailscale's default IPv4 and IPv6 ranges.</summary>
public class TailscaleAddressTests
{
    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fd7a:115c:a1e0:ab12::c401:222b", true)]
    [InlineData("fd7a:115c:a1e1::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:100.64.0.9", true)]
    [InlineData("192.0.2.1", false)]
    [InlineData("not-an-ip", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Classifies_tailnet_ranges(string? ip, bool expected)
    {
        NetworkUtilities.IsTailscaleAddress(ip).Should().Be(expected);
    }
}
