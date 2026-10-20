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
    [InlineData("172.16.5.0/24")]
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

    private static List<string> Cut(string range, params string[] holes) =>
        BlocklistEntry.CutOut(System.Net.IPNetwork.Parse(range), holes.Select(System.Net.IPAddress.Parse))
            .Select(n => n.ToString()).ToList();

    private static System.Numerics.BigInteger Size(string network)
    {
        var n = System.Net.IPNetwork.Parse(network);
        var bits = n.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return System.Numerics.BigInteger.One << (bits - n.PrefixLength);
    }

    [Fact]
    public void CutOut_NoHoleInside_ReturnsRangeWhole()
    {
        Cut("198.51.100.0/24", "203.0.113.7", "2001:db8::1").Should().Equal("198.51.100.0/24");
    }

    [Fact]
    public void CutOut_OneHole_LeavesOnePrefixPerHostBit()
    {
        var pieces = Cut("198.51.100.0/24", "198.51.100.77");

        pieces.Should().HaveCount(8);
        pieces.Should().NotContain(p => System.Net.IPNetwork.Parse(p).Contains(System.Net.IPAddress.Parse("198.51.100.77")));
        pieces.Aggregate(System.Numerics.BigInteger.Zero, (sum, p) => sum + Size(p)).Should().Be(255);
        pieces.Should().Contain(["198.51.100.0/26", "198.51.100.128/25", "198.51.100.76/32", "198.51.100.78/31"]);
    }

    [Fact]
    public void CutOut_TwoHoles_CoversEverythingElse()
    {
        var pieces = Cut("203.0.113.0/24", "203.0.113.63", "203.0.113.219");

        pieces.Aggregate(System.Numerics.BigInteger.Zero, (sum, p) => sum + Size(p)).Should().Be(254);
        pieces.Should().NotContain(p => System.Net.IPNetwork.Parse(p).Contains(System.Net.IPAddress.Parse("203.0.113.63")));
        pieces.Should().NotContain(p => System.Net.IPNetwork.Parse(p).Contains(System.Net.IPAddress.Parse("203.0.113.219")));
        pieces.Should().Contain("203.0.113.64/26");
    }

    [Fact]
    public void CutOut_HoleIsTheWholeRange_LeavesNothing()
    {
        Cut("203.0.113.7/32", "203.0.113.7").Should().BeEmpty();
    }

    [Fact]
    public void CutOut_IPv6()
    {
        var pieces = Cut("2001:db8::/32", "2001:db8::1");

        pieces.Should().HaveCount(96);
        pieces.Aggregate(System.Numerics.BigInteger.Zero, (sum, p) => sum + Size(p))
            .Should().Be((System.Numerics.BigInteger.One << 96) - 1);
    }

    [Fact]
    public void CutOut_MappedHole_MatchesIPv4Range()
    {
        Cut("198.51.100.0/31", "::ffff:198.51.100.1").Should().Equal("198.51.100.0/32");
    }
}
