using FluentAssertions;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class PublicListMatcherTests
{
    private static readonly PublicBlockList Drop = PublicBlockLists.Find(PublicBlockLists.SpamhausDrop)!;
    private static readonly PublicBlockList Feodo = PublicBlockLists.Find(PublicBlockLists.Feodo)!;

    private static PublicListMatcher Matcher() => new(
    [
        ("198.51.100.0/24", Drop),
        ("198.51.100.77", Feodo),
        ("2001:db8:1000::/36", Drop),
    ]);

    [Theory]
    [InlineData("198.51.100.5", PublicBlockLists.SpamhausDrop)]
    [InlineData("198.51.100.77", PublicBlockLists.Feodo)]
    [InlineData("::ffff:198.51.100.77", PublicBlockLists.Feodo)]
    [InlineData("2001:db8:1fff::1", PublicBlockLists.SpamhausDrop)]
    public void Find_MatchesTheMostSpecificEntry(string address, string listId)
    {
        Matcher().Find(address)!.Id.Should().Be(listId);
    }

    [Theory]
    [InlineData("198.51.101.5")]
    [InlineData("2001:db8:2000::1")]
    [InlineData("not an address")]
    [InlineData(null)]
    public void Find_MissesEverythingElse(string? address)
    {
        Matcher().Find(address).Should().BeNull();
    }

    [Fact]
    public void Contact_IsAnOutgoingFlowToAPublicAddress_IncludingIpv6Clients()
    {
        ThreatEvent Flow(string source, string dest, string direction) => new()
        {
            SourceIp = source, DestIp = dest, Direction = direction, EventSource = EventSource.TrafficFlow,
        };

        PublicListContact.IsCandidate(Flow("10.0.0.20", "198.51.100.5", "outgoing")).Should().BeTrue();
        PublicListContact.IsCandidate(Flow("2001:db8:aaaa::20", "198.51.100.5", "outgoing")).Should().BeTrue();
        PublicListContact.IsCandidate(Flow("198.51.100.5", "10.0.0.20", "incoming")).Should().BeFalse();
        PublicListContact.IsCandidate(Flow("10.0.0.20", "10.0.1.1", "outgoing")).Should().BeFalse();
    }

    [Fact]
    public void Tag_BlockedIsAnAttempt_AllowedIsAContact()
    {
        var blocked = new ThreatEvent { Action = ThreatAction.Blocked };
        PublicListContact.Tag(blocked, Feodo);
        blocked.SignatureName.Should().Be("Tried to reach an address on abuse.ch Feodo Tracker");
        blocked.KillChainStage.Should().Be(KillChainStage.PostExploitation);
        blocked.Category.Should().Be(PublicListContact.Category);

        var allowed = new ThreatEvent { Action = ThreatAction.Detected };
        PublicListContact.Tag(allowed, null);
        allowed.SignatureName.Should().Be("Reached an address on a public block list");
        allowed.Severity.Should().BeGreaterThan(blocked.Severity);
    }

    private static ThreatEvent Contact(ThreatAction action, string dest = "198.51.100.77", string protocol = "TCP", int port = 443) => new()
    {
        SourceIp = "10.0.0.20", DestIp = dest, Protocol = protocol, DestPort = port, Action = action,
        Direction = "outgoing", EventSource = EventSource.TrafficFlow,
    };

    private static readonly BlockingException Https = new()
    {
        Direction = ExceptionDirection.Outbound, Device = "10.0.0.20", Address = "198.51.100.77",
        Protocol = ExceptionProtocol.Tcp, PortFrom = 443, PortTo = 443,
    };

    [Fact]
    public void IsExcepted_AllowedContactAnExceptionCovers()
    {
        PublicListContact.IsExcepted(Contact(ThreatAction.Detected), [Https]).Should().BeTrue();
    }

    [Fact]
    public void IsExcepted_NeverABlockedAttempt()
    {
        PublicListContact.IsExcepted(Contact(ThreatAction.Blocked), [Https]).Should().BeFalse();
    }

    [Theory]
    [InlineData("198.51.100.78", "TCP", 443)]
    [InlineData("198.51.100.77", "UDP", 443)]
    [InlineData("198.51.100.77", "TCP", 80)]
    [InlineData("198.51.100.77", "ICMP", 0)]
    public void IsExcepted_NotWhenTheExceptionDoesNotCoverIt(string dest, string protocol, int port)
    {
        PublicListContact.IsExcepted(Contact(ThreatAction.Detected, dest, protocol, port), [Https]).Should().BeFalse();
    }

    [Fact]
    public void IsExcepted_NotByAnInboundException()
    {
        var inbound = Https with { Direction = ExceptionDirection.Inbound };
        PublicListContact.IsExcepted(Contact(ThreatAction.Detected), [inbound]).Should().BeFalse();
    }

    [Fact]
    public void IsExcepted_DevicePortExceptionCoversEveryListedAddress()
    {
        var syncthing = new BlockingException
        {
            Direction = ExceptionDirection.Outbound, Device = "10.0.0.0/24", Protocol = ExceptionProtocol.Tcp, PortFrom = 22067,
        };
        PublicListContact.IsExcepted(Contact(ThreatAction.Detected, "203.0.113.9", port: 22067), [syncthing]).Should().BeTrue();
    }

    [Fact]
    public void IsExcepted_NotWithNoExceptions()
    {
        PublicListContact.IsExcepted(Contact(ThreatAction.Detected), []).Should().BeFalse();
    }
}
