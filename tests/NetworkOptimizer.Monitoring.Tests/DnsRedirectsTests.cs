using FluentAssertions;
using NetworkOptimizer.Monitoring.Conntrack;
using NetworkOptimizer.Monitoring.LiveTraffic;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>A lookup the Gateway DNAT'd to a site resolver: named once, with the resolver that answered.</summary>
public class DnsRedirectsTests
{
    private const string Client = "192.168.1.100";
    private const string Outside = "198.51.100.53";
    private const string SiteResolver = "192.168.30.53";

    private static LiveDnsRecord Lookup(string server, int port = 40000) =>
        new(DateTime.UtcNow, Client, server, "example.com", "A", "NOERROR", 1, true, [], port);

    private static Dictionary<(string, int), (string, string)> Redirects() => DnsRedirects.From(
    [
        ConntrackParser.ParseLine($"ipv4 2 udp 17 29 src={Client} dst={Outside} sport=40000 dport=53 packets=1 bytes=60 src={SiteResolver} dst={Client} sport=53 dport=40000 packets=1 bytes=90 mark=0 use=1")!,
        ConntrackParser.ParseLine($"ipv4 2 udp 17 29 src={Client} dst={Outside} sport=40001 dport=53 packets=1 bytes=60 src={Outside} dst=203.0.113.9 sport=53 dport=40001 packets=1 bytes=90 mark=0 use=1")!,
    ]);

    [Fact]
    public void RedirectedLookupKeepsTheClientLegWithTheResolverThatAnswered()
    {
        var result = DnsRedirects.Apply([Lookup(Outside), Lookup(SiteResolver)], Redirects()).ToList();

        result.Should().ContainSingle();
        result[0].Record.ServerIp.Should().Be(Outside);
        result[0].RedirectedTo.Should().Be(SiteResolver);
    }

    [Fact]
    public void LookupThatWentOutUnchangedIsNotRedirected()
    {
        // The second flow is only source-NAT'd out the WAN: it reached the server it asked.
        var result = DnsRedirects.Apply([Lookup(Outside, 40001)], Redirects()).ToList();

        result.Should().ContainSingle().Which.RedirectedTo.Should().BeEmpty();
    }

    [Fact]
    public void LookupSentAfterTheSnapshotWaitsForTheNextOne()
    {
        var snapshot = DateTime.UtcNow - TimeSpan.FromSeconds(5);
        // Port 40002: its conntrack entry was created after the snapshot was read.
        var late = Lookup(Outside, 40002);
        var later = new List<LiveDnsRecord>();

        DnsRedirects.Apply([], [late], Redirects(), snapshot, later).Should().BeEmpty("it has no entry in this snapshot yet");
        later.Should().ContainSingle();

        var next = DnsRedirects.From(
        [
            ConntrackParser.ParseLine($"ipv4 2 udp 17 29 src={Client} dst={Outside} sport=40002 dport=53 packets=1 bytes=60 src={SiteResolver} dst={Client} sport=53 dport=40002 packets=1 bytes=90 mark=0 use=1")!,
        ]);
        var again = new List<LiveDnsRecord>();
        DnsRedirects.Apply(later, [], next, DateTime.UtcNow, again).Should().ContainSingle()
            .Which.RedirectedTo.Should().Be(SiteResolver);
        again.Should().BeEmpty();
    }

    [Fact]
    public void HeldLookupWithNoEntryInTheNextSnapshotEitherIsNotRedirected()
    {
        var later = new List<LiveDnsRecord>();
        DnsRedirects.Apply([Lookup(Outside, 40003)], [], Redirects(), DateTime.UtcNow, later)
            .Should().ContainSingle().Which.RedirectedTo.Should().BeEmpty();
        later.Should().BeEmpty("a held lookup is never held twice");
    }

    [Fact]
    public void LookupFromBeforeTheSnapshotIsReadAtOnce()
    {
        var old = Lookup(Outside, 40001) with { At = DateTime.UtcNow - TimeSpan.FromSeconds(10) };
        var later = new List<LiveDnsRecord>();

        DnsRedirects.Apply([], [old], Redirects(), DateTime.UtcNow, later).Should().ContainSingle();
        later.Should().BeEmpty();
    }
}
