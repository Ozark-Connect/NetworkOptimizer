using FluentAssertions;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class IpsOutboundDecisionTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 16, 0, 0, DateTimeKind.Utc);
    private static readonly BlockingSettings Enforce = new() { Mode = BlockingMode.Enforce };
    private static readonly BlockAllowlist NoAllowlist = new(null);
    private static readonly IReadOnlySet<string> NoActive = new HashSet<string>();
    private static readonly IReadOnlyDictionary<string, int> NoStrikes = new Dictionary<string, int>();

    private static IpsOutboundAlert Alert(
        string signature = "ET MALWARE Example Checkin",
        string category = "A Network Trojan was detected",
        string ubnt = "MALWARE",
        int severity = 1,
        string confidence = "High",
        string[]? targets = null,
        string remote = "203.0.113.10",
        DateTime? at = null) => new()
    {
        ClientIp = "192.168.1.50",
        ClientMac = "aa:bb:cc:dd:ee:ff",
        RemoteIp = remote,
        RemotePort = 80,
        Protocol = "tcp",
        SignatureId = 2000001,
        Signature = signature,
        Category = category,
        UbntCategory = ubnt,
        Severity = severity,
        Confidence = confidence,
        AttackTargets = targets ?? [],
        Timestamp = at ?? Now,
        FromAgent = true,
    };

    private static IpsOutboundOutcome Decide(IpsOutboundAlert alert, BlockingSettings? settings = null,
        IReadOnlySet<string>? active = null, IReadOnlyDictionary<string, int>? strikes = null,
        BlockAllowlist? allowlist = null, int? asn = null) =>
        IpsOutboundDecision.Decide(alert, "site-a", settings ?? Enforce, Now, active ?? NoActive, strikes ?? NoStrikes,
            allowlist ?? NoAllowlist, asn);

    [Theory]
    [InlineData("ET CNC Feodo Tracker Reported CnC Server group 1", "A Network Trojan was detected", "BOTCC")]
    [InlineData("ET MALWARE X2000M.Agent Checkin", "Malware Command and Control Activity Detected", "MALWARE")]
    public void CommandAndControl_IsItsOwnClass(string signature, string category, string ubnt) =>
        IpsOutboundDecision.Classify(Alert(signature, category, ubnt)).Should().Be(IpsAlertClass.CommandAndControl);

    [Theory]
    [InlineData("MALWARE")]
    [InlineData("MOBILE_MALWARE")]
    [InlineData("COMPROMISED")]
    [InlineData("CIARMY")]
    [InlineData("WEB_CLIENT")]
    [InlineData("USER_AGENTS")]
    public void RemoteThreatGroups_AreThreats(string ubnt) =>
        IpsOutboundDecision.Classify(Alert(ubnt: ubnt)).Should().Be(IpsAlertClass.RemoteThreat);

    [Theory]
    [InlineData("SCAN")]
    [InlineData("EXPLOIT")]
    [InlineData("WEB_SERVER")]
    [InlineData("DOS")]
    public void AttackingGroups_NeverBlockTheVictim(string ubnt) =>
        IpsOutboundDecision.Classify(Alert(ubnt: ubnt, category: "Attempted Administrator Privilege Gain")).Should().Be(IpsAlertClass.DeviceAttacking);

    [Fact]
    public void AttackTarget_WinsOverTheGroup()
    {
        // A browser exploit filed under EXPLOIT: the rule says the client is the target, so the remote attacks.
        IpsOutboundDecision.Classify(Alert(ubnt: "EXPLOIT", targets: ["Client_Endpoint"])).Should().Be(IpsAlertClass.RemoteThreat);
        // A malware-group rule aimed at a server: the device on the site is the one attacking.
        IpsOutboundDecision.Classify(Alert(ubnt: "MALWARE", targets: ["Web_Server"])).Should().Be(IpsAlertClass.DeviceAttacking);
    }

    [Theory]
    [InlineData("ET POLICY curl User-Agent Outbound", "Potential Corporate Privacy Violation", "POLICY", 2, "High")]
    [InlineData("ET INFO Observed DNS Query to .cloud TLD", "Misc activity", "INFO", 2, "High")]
    [InlineData("ET MALWARE Low Confidence Thing", "A Network Trojan was detected", "MALWARE", 1, "Low")]
    [InlineData("ET MALWARE Low Severity Thing", "A Network Trojan was detected", "MALWARE", 3, "High")]
    [InlineData("ET P2P BitTorrent peer sync", "Potential Corporate Privacy Violation", "P2P", 2, "")]
    public void Noise_NeverBlocks(string signature, string category, string ubnt, int severity, string confidence) =>
        IpsOutboundDecision.Classify(Alert(signature, category, ubnt, severity, confidence)).Should().Be(IpsAlertClass.Noise);

    [Fact]
    public void ConsoleAlert_WithoutGroup_IsAThreatOnlyAtSeverityOne()
    {
        IpsOutboundDecision.Classify(Alert(ubnt: "", severity: 1)).Should().Be(IpsAlertClass.RemoteThreat);
        IpsOutboundDecision.Classify(Alert(ubnt: "", severity: 2)).Should().Be(IpsAlertClass.Noise);
    }

    [Fact]
    public void Threat_BlocksTheRemoteOutboundWithEvidence()
    {
        var outcome = Decide(Alert());
        var block = outcome.Block!;
        block.SourceIp.Should().Be("203.0.113.10");
        block.Direction.Should().Be(BlockDirection.Outbound);
        block.Trigger.Should().Be(BlockTrigger.IpsOutbound);
        block.Enforced.Should().BeTrue();
        block.ExpiresAt.Should().Be(Now.AddMinutes(Enforce.BanMinutes));
        var evidence = IpsBlockEvidence.Parse(block.Evidence)!;
        evidence.ClientIp.Should().Be("192.168.1.50");
        evidence.OriginSite.Should().Be("site-a");
        evidence.Class.Should().Be(IpsAlertClass.RemoteThreat);
    }

    [Fact]
    public void CommandAndControl_StartsAtADayAndTakesStrikes()
    {
        var c2 = Alert(category: "Malware Command and Control Activity Detected");
        Decide(c2).Block!.ExpiresAt.Should().Be(Now.AddMinutes(IpsOutboundDecision.CommandAndControlBanMinutes));
        Decide(c2, strikes: new Dictionary<string, int> { ["203.0.113.10"] = 1 }).Block!.ExpiresAt
            .Should().Be(Now.AddMinutes(IpsOutboundDecision.CommandAndControlBanMinutes * BlockDecisionEngine.StrikeMultiplier));
        Decide(c2, strikes: new Dictionary<string, int> { ["203.0.113.10"] = 2 }).Block!.ExpiresAt
            .Should().Be(Now.AddMinutes(BlockingSettings.MaxBanMinutes));
    }

    [Fact]
    public void DryRun_RecordsWithoutEnforcing() =>
        Decide(Alert(), new BlockingSettings { Mode = BlockingMode.DryRun }).Block!.Enforced.Should().BeFalse();

    [Fact]
    public void Off_OrToggledOff_BlocksNothing()
    {
        Decide(Alert(), new BlockingSettings { Mode = BlockingMode.Off }).Block.Should().BeNull();
        Decide(Alert(), Enforce with { IpsOutbound = false }).Block.Should().BeNull();
    }

    [Fact]
    public void SharedHosting_IsNeverBlocked()
    {
        var outcome = Decide(Alert(category: "Malware Command and Control Activity Detected"), asn: 13335);
        outcome.Block.Should().BeNull();
        outcome.Skipped.Should().Contain("AS13335");
    }

    [Fact]
    public void AllowlistedOrAlreadyBlocked_IsSkipped()
    {
        Decide(Alert(), allowlist: new BlockAllowlist("203.0.113.0/24")).Block.Should().BeNull();
        Decide(Alert(), active: new HashSet<string> { "203.0.113.10" }).Skipped.Should().Be("Already blocked");
    }

    [Fact]
    public void PrivateRemote_IsSkipped() =>
        Decide(Alert(remote: "10.0.0.5")).Block.Should().BeNull();

    [Fact]
    public void OldAlert_FromBackfill_BlocksNothing() =>
        Decide(Alert(at: Now - IpsOutboundDecision.MaxAlertAge - TimeSpan.FromMinutes(1))).Block.Should().BeNull();

    [Fact]
    public void ConsoleEvent_OnlyOutboundIpsEventsBecomeAlerts()
    {
        var evt = new ThreatEvent
        {
            EventSource = EventSource.Ips, SignatureId = 2000001, SignatureName = "ET MALWARE Example", Category = "A Network Trojan was detected",
            Severity = 5, SourceIp = "192.168.1.50", DestIp = "203.0.113.10", DestPort = 443, Protocol = "TCP", Timestamp = Now,
        };
        var alert = IpsOutboundDecision.FromConsoleEvent(evt)!;
        alert.Severity.Should().Be(1);
        alert.FromAgent.Should().BeFalse();
        alert.Protocol.Should().Be("tcp");

        IpsOutboundDecision.FromConsoleEvent(new ThreatEvent { EventSource = EventSource.Ips, SignatureId = 1, SourceIp = "203.0.113.10", DestIp = "192.168.1.50" })
            .Should().BeNull("an inbound alert is not outbound");
        IpsOutboundDecision.FromConsoleEvent(new ThreatEvent { EventSource = EventSource.Waf, SignatureId = 1, SourceIp = "192.168.1.50", DestIp = "203.0.113.10" })
            .Should().BeNull();
    }

    [Fact]
    public void DesiredSet_KeepsAnAddressBlockedBothWays()
    {
        ThreatBlock Block(BlockDirection d, int hours) => new()
        {
            SourceIp = "203.0.113.10", Direction = d, Enforced = true, CreatedAt = Now.AddHours(-1), ExpiresAt = Now.AddHours(hours),
        };
        var set = BlockDecisionEngine.DesiredSet([Block(BlockDirection.Inbound, 1), Block(BlockDirection.Inbound, 3), Block(BlockDirection.Outbound, 2)], Now, 10);
        set.Should().HaveCount(2);
        set.Single(b => b.Direction == BlockDirection.Inbound).ExpiresAt.Should().Be(Now.AddHours(3));
    }

    [Fact]
    public void ForOtherSites_DropsTheDeviceButKeepsTheSignature()
    {
        var block = Decide(Alert()).Block!;
        var elsewhere = IpsBlockEvidence.Parse(block.ForOtherSites().Evidence)!;
        elsewhere.ClientIp.Should().BeEmpty();
        elsewhere.ClientMac.Should().BeNull();
        elsewhere.Signature.Should().Be("ET MALWARE Example Checkin");
        block.ForOtherSites().Reason.Should().Be(block.Reason);
        IpsBlockEvidence.Parse(block.Evidence)!.ClientIp.Should().Be("192.168.1.50", "the original is untouched");
    }

    [Fact]
    public void AgentFingerprint_ChangesWithDirection()
    {
        var inbound = new ThreatBlock { SourceIp = "203.0.113.10", ExpiresAt = Now.AddHours(1) };
        var outbound = new ThreatBlock { SourceIp = "203.0.113.10", ExpiresAt = Now.AddHours(1), Direction = BlockDirection.Outbound };
        ThreatBlockingService.AgentFingerprint(true, [inbound], []).Should().NotBe(ThreatBlockingService.AgentFingerprint(true, [outbound], []));
    }

    [Fact]
    public void DedupKey_MatchesAcrossSources()
    {
        var agent = Alert();
        var console = agent with { FromAgent = false, Timestamp = Now.AddSeconds(50), ClientMac = null };
        console.DedupKey.Should().Be(agent.DedupKey);
    }
}
