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
}
