using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests;

/// <summary>
/// The single Console-entry presence definition. Every rule here is load-bearing at both entry
/// points (topology discovery and the Wi-Fi Optimizer roster), so the surfaces they feed agree.
/// </summary>
public class ClientPresenceTests
{
    private const string ApMac = "aa:bb:cc:dd:ee:ff";

    [Theory]
    [InlineData(null, true)]   // a missing field is not evidence of absence
    [InlineData(0L, true)]
    [InlineData(600L, true)]   // at the threshold
    [InlineData(601L, false)]
    [InlineData(292718L, false)]
    public void IsPresent_JudgesByIdleTolerance(long? idle, bool expected)
    {
        ClientPresence.IsPresent(idle).Should().Be(expected);
    }

    [Fact]
    public void LowestIdle_TakesMinimumAcrossLinks()
    {
        ClientPresence.LowestIdle(new long[] { 4700, 69, 4650 }).Should().Be(69);
        ClientPresence.LowestIdle(System.Array.Empty<long>()).Should().BeNull();
    }

    [Theory]
    [InlineData(ApMac, null, null, false, true)]  // any one field is evidence
    [InlineData(null, "ng", null, false, true)]
    [InlineData(null, null, -62, false, true)]
    [InlineData(null, null, null, true, true)]    // an MLO link is evidence too
    [InlineData(null, null, null, false, false)]  // all empty: the departed blank row
    [InlineData("", "", 0, false, false)]         // a zero signal is not a reading
    public void HasAssociationEvidence_RequiresAllEmptyToDeny(
        string? apMac, string? radio, int? signal, bool hasMlo, bool expected)
    {
        ClientPresence.HasAssociationEvidence(apMac, radio, signal, hasMlo).Should().Be(expected);
    }

    [Fact]
    public void AgentAbsent_BeatsTheNullIdleRule()
    {
        // The departed client the Console still lists with no idle and no fields at all.
        ClientPresence.IsPresent(null, ApMac, "ng", -60, false, AgentClientPresence.Absent)
            .Should().BeFalse();
    }

    [Fact]
    public void AgentPresent_BeatsAStaleConsoleIdle()
    {
        ClientPresence.IsPresent(4000, ApMac, "ng", -60, false, AgentClientPresence.Present)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(600L)]
    [InlineData(601L)]
    [InlineData(292718L)]
    public void AgentUnknown_WithEvidence_MatchesTheIdleRuleExactly(long? idle)
    {
        // The no-agent install's path: an Unknown verdict on a client with association evidence
        // must reproduce today's idle rule bit for bit.
        ClientPresence.IsPresent(idle, ApMac, "ng", -60, false, AgentClientPresence.Unknown)
            .Should().Be(ClientPresence.IsPresent(idle));
    }

    [Fact]
    public void AgentUnknown_FallsBackToIdleAndEvidence()
    {
        ClientPresence.IsPresent(30, ApMac, "ng", -60, false, AgentClientPresence.Unknown)
            .Should().BeTrue();
        ClientPresence.IsPresent(601, ApMac, "ng", -60, false, AgentClientPresence.Unknown)
            .Should().BeFalse();
        // The blank row: null idle passes the tolerance but carries no association evidence.
        ClientPresence.IsPresent(null, null, null, null, false, AgentClientPresence.Unknown)
            .Should().BeFalse();
    }
}
