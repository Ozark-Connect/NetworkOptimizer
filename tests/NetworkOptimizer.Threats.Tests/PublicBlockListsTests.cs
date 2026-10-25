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
    public void Default_IsOnlySpamhausDrop()
    {
        // Only lists whose terms allow any use are on by default.
        PublicBlockLists.DefaultIds.Should().Equal(PublicBlockLists.SpamhausDrop);
    }

    [Fact]
    public void OnlyCommandServerAndCriminalNetworkListsBlockOutbound()
    {
        PublicBlockLists.All.Where(l => l.Outbound).Select(l => l.Id)
            .Should().BeEquivalentTo(PublicBlockLists.SpamhausDrop, PublicBlockLists.ThreatFox, PublicBlockLists.QFeeds, PublicBlockLists.Feodo);
    }

    [Fact]
    public void ThreatFox_KeepsConfidentC2Ips_WithoutHijackedHosts()
    {
        var body = """
            # "first_seen_utc","ioc_id","ioc_value","ioc_type","threat_type","fk_malware","malware_alias","malware_printable","last_seen_utc","confidence_level","is_compromised","reference","tags","anonymous","reporter"
            "2026-10-16 14:05:05", "1", "203.0.113.5:443", "ip:port", "botnet_cc", "win.havoc", "None", "Havoc", "", "100", "False", "None", "havoc", "1", "anonymous"
            "2026-10-16 14:05:05", "2", "203.0.113.6:22", "ip:port", "botnet_cc", "unknown", "None", "Unknown malware", "", "100", "True", "None", "", "1", "anonymous"
            "2026-10-16 14:05:05", "3", "203.0.113.7:8080", "ip:port", "botnet_cc", "unknown", "None", "Unknown malware", "", "50", "False", "None", "", "1", "anonymous"
            "2026-10-16 14:05:05", "4", "[2001:db8::9]:443", "ip:port", "botnet_cc", "unknown", "None", "Unknown malware", "", "75", "False", "None", "", "1", "anonymous"
            "2026-10-16 14:05:05", "5", "203.0.113.5:8443", "ip:port", "botnet_cc", "win.havoc", "None", "Havoc", "", "100", "False", "None", "havoc", "1", "anonymous"
            "2026-10-16 14:05:05", "6", "203.0.113.8:8081", "ip:port", "botnet_cc", "win.cobalt_strike", "Agentemis,BEACON,CobaltStrike,cobeacon", "Cobalt Strike", "", "100", "False", "None", "c2,cobaltstrike", "1", "anonymous"
            "2026-10-16 14:05:05", "7", "203.0.113.9:8081", "ip:port", "botnet_cc", "win.cobalt_strike", "Agentemis,BEACON,CobaltStrike,cobeacon", "Cobalt Strike", "", "100", "True", "None", "cobaltstrike", "1", "anonymous"
            """;

        PublicBlockLists.ParseThreatFoxIpPort(body).Distinct().Should().BeEquivalentTo("203.0.113.5", "2001:db8::9", "203.0.113.8");
    }

    [Fact]
    public void EveryTokenListSaysHowItSendsTheToken()
    {
        PublicBlockLists.Find(PublicBlockLists.QFeeds)!.TokenUser.Should().Be("api_token");
        PublicBlockLists.Find(PublicBlockLists.ThreatFox)!.TokenHeader.Should().Be("Auth-Key");
        PublicBlockLists.All.Where(l => l.NeedsToken).Should().OnlyContain(l => !l.DefaultOn);
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
            .Should().Be("Includes the public block list Spamhaus DROP.");
        PublicBlockLists.RecommendedNote([PublicBlockLists.SpamhausDrop, PublicBlockLists.Feodo, PublicBlockLists.DShield])
            .Should().Be("Includes the public block lists Spamhaus DROP, abuse.ch Feodo Tracker, and DShield Top 20.");
    }

    [Fact]
    public void CutAroundProbed_LeavesEntriesWithoutTargetsAlone()
    {
        System.Net.IPAddress[] probed = [System.Net.IPAddress.Parse("192.0.2.1")];
        ThreatBlockingService.CutAroundProbed("198.51.100.0/24", probed).Should().Equal("198.51.100.0/24");
        ThreatBlockingService.CutAroundProbed("203.0.113.5", probed).Should().Equal("203.0.113.5");
        ThreatBlockingService.CutAroundProbed("198.51.100.0/24", []).Should().Equal("198.51.100.0/24");
    }

    [Fact]
    public void CutAroundProbed_DropsAnEntryThatIsATarget()
    {
        ThreatBlockingService.CutAroundProbed("203.0.113.5", [System.Net.IPAddress.Parse("203.0.113.5")]).Should().BeEmpty();
    }

    [Fact]
    public void CutAroundProbed_CutsTargetsOutOfARange()
    {
        var pieces = ThreatBlockingService.CutAroundProbed("198.51.100.0/24",
            [System.Net.IPAddress.Parse("198.51.100.63"), System.Net.IPAddress.Parse("198.51.100.219")]);

        pieces.Should().Contain(["198.51.100.64/26", "198.51.100.62/32", "198.51.100.218/32"]);
        pieces.Should().NotContain(p => System.Net.IPNetwork.Parse(p).Contains(System.Net.IPAddress.Parse("198.51.100.63")));
        pieces.Should().NotContain(p => System.Net.IPNetwork.Parse(p).Contains(System.Net.IPAddress.Parse("198.51.100.219")));
        pieces.Should().OnlyContain(p => PublicBlockLists.Normalize(p) == p);
    }
}