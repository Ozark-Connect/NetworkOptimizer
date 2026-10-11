using FluentAssertions;
using NetworkOptimizer.Threats.Blocking;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class PublicBlockListsTests
{
    [Fact]
    public void Spamhaus_ReadsCidrLines_SkipsMetadata()
    {
        var body = """
            {"cidr":"198.51.100.0/24","sblid":"SBL1","rir":"arin"}
            {"cidr":"2001:db8:bad::/48","sblid":"SBL2","rir":"ripencc"}
            {"type":"metadata","timestamp":1760000000,"size":2}
            """;
        PublicBlockLists.ParseSpamhausJson(body).Should().Equal("198.51.100.0/24", "2001:db8:bad::/48");
    }

    [Fact]
    public void IpLines_SkipCommentsAndJunk()
    {
        var body = "# Feodo Tracker\n203.0.113.5\n203.0.113.6 # c2\nnot-an-ip\n\n";
        PublicBlockLists.ParseIpLines(body).Should().Equal("203.0.113.5", "203.0.113.6");
    }

    [Fact]
    public void DShield_BuildsRangesFromStartAndBits()
    {
        var body = "# comment\nStart\tEnd\tNetblock\tAttacks\n198.51.100.0\t198.51.100.255\t24\t1234\n";
        PublicBlockLists.ParseDShield(body).Should().Equal("198.51.100.0/24");
    }

    [Theory]
    [InlineData("198.51.100.77/24", "198.51.100.0/24")]
    [InlineData("2001:DB8::1/64", "2001:db8::/64")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    [InlineData("203.0.113.5/33", null)]
    [InlineData("nonsense", null)]
    public void Normalize(string input, string? expected)
    {
        PublicBlockLists.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Defaults_AreSpamhausAndFeodo()
    {
        PublicBlockLists.DefaultIds.Should().BeEquivalentTo(PublicBlockLists.SpamhausDrop, PublicBlockLists.Feodo);
    }

    [Theory]
    [InlineData("10.0.0.0/8")]          // a bogon a list must never push
    [InlineData("100.64.0.0/10")]       // CGNAT
    [InlineData("0.0.0.0/0")]
    [InlineData("fd00::/8")]            // ULA
    [InlineData("fe80::/10")]
    [InlineData("104.16.0.0/16")]       // inside Cloudflare
    [InlineData("104.0.0.0/8")]         // contains Cloudflare
    [InlineData("198.51.100.0/24")]     // contains a trusted address
    public void ExemptsRange_RefusesAnyOverlap(string entry)
    {
        var allowlist = new BlockAllowlist(null, wanAddresses: ["198.51.100.7"]);
        allowlist.ExemptsRange(entry).Should().NotBeNull();
    }

    [Theory]
    [InlineData("203.0.113.0/24")]
    [InlineData("2001:db8:bad::/48")]
    [InlineData("203.0.113.5")]
    public void ExemptsRange_AllowsPublicRanges(string entry)
    {
        var allowlist = new BlockAllowlist(null, wanAddresses: ["198.51.100.7"]);
        allowlist.ExemptsRange(entry).Should().BeNull();
    }

    [Fact]
    public void SplitFamilies_KeepsRangesAndSeparatesVersions()
    {
        var (v4, v6) = GatewayBlocklist.SplitFamilies(["198.51.100.0/24", "203.0.113.5", "2001:db8::/32", "junk"]);
        v4.Should().Equal("198.51.100.0/24", "203.0.113.5");
        v6.Should().Equal("2001:db8::/32");
    }

    [Fact]
    public void RecommendedNote_NamesTheChosenLists()
    {
        PublicBlockLists.RecommendedNote([]).Should().BeNull();
        PublicBlockLists.RecommendedNote([PublicBlockLists.Feodo]).Should().Be("Includes the public block list abuse.ch Feodo Tracker.");
        PublicBlockLists.RecommendedNote(PublicBlockLists.DefaultIds)
            .Should().Be("Includes the public block lists Spamhaus DROP and abuse.ch Feodo Tracker.");
        PublicBlockLists.RecommendedNote([PublicBlockLists.SpamhausDrop, PublicBlockLists.Feodo, PublicBlockLists.DShield])
            .Should().Be("Includes the public block lists Spamhaus DROP, abuse.ch Feodo Tracker, and DShield Top 20.");
    }
}