using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The membership ledger behind the agent presence verdict. The rules under test are the ones
/// that keep the verdict safe: Absent needs a listing or a fresh non-empty answer, Present
/// reaches across access points, a newer association discards a rival claim and the discard
/// sticks, and moving counters keep a quiet client vouched for.
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
        string? ip = null,
        long txBytes = 0)
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
                new() { Mac = linkMac ?? mac, IdleSeconds = idle, Active = true, TxBytes = txBytes },
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
    public void StaleListing_IsPositiveAbsence_EvenWithNoClaimedAp()
    {
        // The observed phantom: an access point held a silent departure's association for hours
        // (idle 8613 s, unauthorized). Asked with NO claimed access point - the exact call that
        // used to fall through to Unknown and let the Console re-supply the phantom's AP and RF.
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(idle: 8613, authorized: false), Client(mac: "0a:0b:0c:0d:0e:0f") }, Now);

        ledger.PresenceFor(null, StationMac, Now).Should().Be(AgentClientPresence.Absent);
        ledger.PresenceFor(Ap2, StationMac, Now).Should().Be(AgentClientPresence.Absent);
    }

    [Fact]
    public void StaleOnOneAp_LiveOnAnother_IsPresent()
    {
        // The roam guard must survive the stale set: the access point a client left keeps a dead
        // entry for it while the one it moved to holds it live.
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(idle: 8613, authorized: false), Client(mac: "0a:0b:0c:0d:0e:0f") }, Now);
        ledger.Record(Ap2, new[] { Client() }, Now);

        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Present);
        ledger.PresenceFor(null, StationMac, Now).Should().Be(AgentClientPresence.Present);
    }

    [Fact]
    public void UnauthorizedListing_IsAbsenceEvidence_LikeStale()
    {
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(authorized: false), Client(mac: "0a:0b:0c:0d:0e:0f") }, Now);

        ledger.PresenceFor(null, StationMac, Now).Should().Be(AgentClientPresence.Absent);
    }

    [Fact]
    public void UnlistedEverywhere_WithNoClaimedAp_IsUnknown()
    {
        // The mass-drop guard is unchanged: no answer listing the client, live or stale, is only
        // ambiguity - never absence.
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client() }, Now);

        ledger.PresenceFor(null, "ff:ff:ff:00:00:99", Now).Should().Be(AgentClientPresence.Unknown);
    }

    [Fact]
    public void SupersededClaim_CannotResurrect_WhenTheRealAssociationEnds()
    {
        // The bounce, end to end: the client roamed off Ap1, whose driver never aged the entry
        // out (idle climbing, under every threshold), and Ap2 holds it live. The fresh claim
        // discards Ap1's - and the discard sticks, so when the client then leaves the ESS from
        // Ap2 there is nothing left to resurrect it.
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(idle: 300) }, Now);
        ledger.Record(Ap2, new[] { Client(idle: 0) }, Now);
        ledger.Record(Ap1, new[] { Client(idle: 303) }, Now);

        ledger.IsClaimSuperseded(Ap1, StationMac).Should().BeTrue();
        ledger.PresenceFor(null, StationMac, Now).Should().Be(AgentClientPresence.Present, "Ap2 still holds it live");

        ledger.Record(Ap2, System.Array.Empty<ApAgentClient>(), Now);
        ledger.PresenceFor(null, StationMac, Now).Should().Be(AgentClientPresence.Absent);
    }

    [Fact]
    public void RoamDoubleClaim_InsideTheGraceWindow_NeverDiscards()
    {
        // A real roam's double-claim skew is a few seconds (absent grace plus poll offsets),
        // an order of magnitude under the discard margin. Nothing may fire inside it.
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(idle: 8) }, Now);
        ledger.Record(Ap2, new[] { Client(idle: 0) }, Now);
        ledger.Record(Ap1, new[] { Client(idle: 9) }, Now);

        ledger.IsClaimSuperseded(Ap1, StationMac).Should().BeFalse();
        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Present);
    }

    [Fact]
    public void RoamingBack_ClearsTheDiscard()
    {
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(idle: 300) }, Now);
        ledger.Record(Ap2, new[] { Client(idle: 0) }, Now);
        ledger.IsClaimSuperseded(Ap1, StationMac).Should().BeTrue();

        // A new association on Ap1: idle back to zero. The discard must lift at once.
        ledger.Record(Ap1, new[] { Client(idle: 0) }, Now);
        ledger.IsClaimSuperseded(Ap1, StationMac).Should().BeFalse();
        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Present);
    }

    [Fact]
    public void QuietClient_PastTheIdleTolerance_StaysVouched_WhileCountersMove()
    {
        // The quiet-device guarantee: counters that CHANGE between readings mean the client is
        // alive, whatever its idle says.
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(idle: 620, txBytes: 1_000) }, Now);
        ledger.Record(Ap1, new[] { Client(idle: 650, txBytes: 1_500) }, Now + TimeSpan.FromSeconds(30));

        ledger.PresenceFor(Ap1, StationMac, Now + TimeSpan.FromSeconds(30)).Should().Be(AgentClientPresence.Present);
    }

    [Fact]
    public void FrozenCounters_DoNotVouch_HoweverManyBytesTheyEverCarried()
    {
        // The MLO trap: a link that carried bytes at association and froze must read dead. Only
        // movement counts, never totals.
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(idle: 620, txBytes: 1_000_000) }, Now);
        ledger.Record(Ap1, new[] { Client(idle: 650, txBytes: 1_000_000) }, Now + TimeSpan.FromSeconds(30));

        ledger.PresenceFor(Ap1, StationMac, Now + TimeSpan.FromSeconds(30)).Should().Be(AgentClientPresence.Absent);
    }

    [Fact]
    public void AuthorizedQuietClient_AtTheThreshold_IsNeverAbsenceEvidence()
    {
        // The deep-sleep safety pin: the stale set uses the SAME MaxIdleSeconds rule as the member
        // gate, so a genuinely associated quiet client is vouched for exactly as it is today.
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client(idle: ClientPresence.MaxIdleSeconds) }, Now);

        ledger.PresenceFor(Ap1, StationMac, Now).Should().Be(AgentClientPresence.Present);
        ledger.PresenceFor(null, StationMac, Now).Should().Be(AgentClientPresence.Present);
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
    public void Record_ReportsWhoJoinedAndLeft()
    {
        // The departure side of the delta is what makes the roster nudge immediate, so its
        // contents are load-bearing, not just the changed flag.
        var ledger = new ApAgentMembershipLedger();
        ledger.Record(Ap1, new[] { Client() }, Now, out var first);
        first.Joined.Should().BeEmpty("the first answer is a baseline");
        first.Left.Should().BeEmpty();

        ledger.Record(Ap1, new[] { Client(mac: "0a:0b:0c:0d:0e:0f") }, Now, out var delta);
        delta.Joined.Should().BeEquivalentTo("0a:0b:0c:0d:0e:0f");
        delta.Left.Should().BeEquivalentTo(StationMac);
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
