using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class BlocklistEntryTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7", false)]
    [InlineData(" 203.0.113.7/32 ", "203.0.113.7", false)]
    [InlineData("198.51.100.77/24", "198.51.100.0/24", false)]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7", false)]
    [InlineData("2001:db8::1", "2001:db8::1", true)]
    [InlineData("2001:db8:1:2::/48", "2001:db8:1::/48", true)]
    public void Canonical_PublicEntries(string entry, string expected, bool v6)
    {
        BlocklistEntry.Canonical(entry).Should().Be((expected, v6));
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.1.0/24")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::1")]
    [InlineData("224.0.0.1")]
    public void Canonical_RefusesNonPublic(string entry)
    {
        BlocklistEntry.Canonical(entry).Should().BeNull();
    }

    [Theory]
    [InlineData("8.0.0.0/7")]
    [InlineData("0.0.0.0/0")]
    [InlineData("2000::/3")]
    [InlineData("203.0.113.7/33")]
    [InlineData("203.0.113.7/x")]
    [InlineData("not an address")]
    [InlineData("")]
    [InlineData(null)]
    public void Canonical_RefusesTooWideOrMalformed(string? entry)
    {
        BlocklistEntry.Canonical(entry).Should().BeNull();
    }
}
