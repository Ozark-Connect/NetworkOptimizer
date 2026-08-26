using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The membership ledger behind the agent presence verdict. The rules under test are the ones
/// that keep the verdict safe: Absent needs a fresh non-empty answer, Present reaches across
/// access points, and the agent's own stale associations are never vouched for.
/// </summary>
public class ApAgentMembershipLedgerTests
{
    private const string Ap1 = "aa:bb:cc:00:00:01";
    private const string Ap2 = "aa:bb:cc:00:00:02";
    private const string StationMac = "00:11:22:33:44:55";
    private const string MldMac = "00:11:22:33:44:60";
    private const string LinkMac = "00:11:22:33:44:61";

    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static ApAgentClient Client(
        string mac = StationMac,
        string? mldMac = null,
        string? linkMac = null,
        long idle = 5,
        bool authorized = true,
        string? hostname = null,
        string? ip = null)
        => new()
        {
            Key = mldMac ?? mac,
            Mac = mac,
            MldMac = mldMac,
            Authorized = authorized,
            Hostname = hostname,
            Ip = ip,
            Links = new List<ApAgentClientLink>
            {
                new() { Mac = linkMac ?? mac, IdleSeconds = idle, Active = true },
            },
        };

    [Fact]
    public void NothingRecorded_IsUnknown_SoNonAgentInstallsAreUntouched()
    {
        // The majority install runs no AP Agents: the ledger never records, every verdict is
        // Unknown, and the Console entry points behave exactly as they do today.
        var ledger = new ApAgentMembershipLedger();
        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Unknown);
        ledger.FindByIp("192.0.2.10", Now).Should().BeNull();
    }

    [Fact]
    public void Member_IsPresent_ByKeyMldAndLinkMac()
    {
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(mldMac: MldMac, linkMac: LinkMac) }, Now);

        ledger.PresenceFor(Ap1, MldMac, Now).Should().Be(AgentClientPresence.Present);
        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Present);
        ledger.PresenceFor(Ap1, LinkMac, Now).Should().Be(AgentClientPresence.Present);
    }

    [Fact]
    public void NonMember_IsAbsent_OnlyWhenTheAnswerNamedClients()
    {
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client() }, Now);

        ledger.PresenceFor(Ap1, "ff:ff:ff:00:00:99", Now).Should().Be(AgentClientPresence.Absent);
    }

    [Fact]
    public void EmptyAnswer_IsUnknown_SoARestartCannotMassDrop()
    {
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, System.Array.Empty<ApAgentClient>(), Now);

        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Unknown);
    }

    [Fact]
    public void StaleOrReleasedAnswer_IsUnknown()
    {
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client() }, Now);

        ledger.PresenceFor(Ap1, StationMac, Now + ApAgentMembershipLedger.AnswerTtl + TimeSpan.FromSeconds(1))
            .Should().Be(AgentClientPresence.Unknown);

        ledger.Release(Ap1);
        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Unknown);
    }

    [Fact]
    public void RoamedClient_IsPresent_WhileTheConsoleStillNamesTheOldAp()
    {
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(mac: "0a:0b:0c:0d:0e:0f") }, Now);
        ledger.Record(Ap2, new[] { Client() }, Now);

        // The Console still says Ap1; Ap2's agent holds it. Present, or the map would blink.
        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Present);
    }

    [Fact]
    public void StaleAssociation_InTheAgentsOwnTable_IsNotVouchedFor()
    {
        // The measured MLO phantom: still listed, 80 minutes idle. It must read Absent.
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(idle: 4800), Client(mac: "0a:0b:0c:0d:0e:0f") }, Now);

        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Absent);
    }

    [Fact]
    public void UnauthorizedClient_IsNotAMember_WhenTheFlagIsReported()
    {
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(authorized: false), Client(mac: "0a:0b:0c:0d:0e:0f") }, Now);

        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Absent);
    }

    [Fact]
    public void UnreportedAuthorizedFlag_DropsNobody()
    {
        // Firmware that never sets the flag leaves every client false; membership must not empty.
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(authorized: false), Client(mac: "0a:0b:0c:0d:0e:0f", authorized: false) }, Now);

        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Present);
    }

    [Fact]
    public void Record_ReportsMembershipChanges_ButNotTheFirstAnswer()
    {
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client() }, Now).Should().BeFalse("the first answer is a baseline, not churn");
        ledger.Record(Ap1, new[] { Client() }, Now).Should().BeFalse();
        ledger.Record(Ap1, System.Array.Empty<ApAgentClient>(), Now).Should().BeTrue("the client departed");
        ledger.Record(Ap1, new[] { Client() }, Now).Should().BeTrue("the client returned");
    }

    [Fact]
    public void FindByIp_ReturnsTheFreshMember()
    {
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(hostname: "TestUser-Phone", ip: "192.0.2.10") }, Now);

        var known = ledger.FindByIp("192.0.2.10", Now);
        known.Should().NotBeNull();
        known!.ClientMac.Should().Be(StationMac);
        known.ApMac.Should().Be(Ap1);
        known.Hostname.Should().Be("TestUser-Phone");

        ledger.FindByIp("192.0.2.99", Now).Should().BeNull();
        ledger.FindByIp("192.0.2.10", Now + ApAgentMembershipLedger.AnswerTtl + TimeSpan.FromSeconds(1))
            .Should().BeNull();
    }
}
