using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

public class WanFloodRulesTests
{
    private static readonly WanFloodRules.Limits Limits = new(2000, 4000, 500, 1000, 1000, 2000, 100, 500);

    private static readonly WanFloodRules.ChainState Suspected = new(true, true, true, true,
        [new WanFloodRules.PortLimit("udp", 51820, 200)], [new WanFloodRules.ForwardCap("tcp", "192.0.2.10", 443)]);

    private static IEnumerable<string> AllRules(WanFloodRules.ChainState state) =>
        new[] { false, true }.SelectMany(v6 => WanFloodRules.Rules(v6, Limits, state).Concat(WanFloodRules.ForwardRules(v6, Limits, state)));

    [Fact]
    public void JumpsOnlyTakeNewConnections()
    {
        // Replies (ESTABLISHED/RELATED) must never enter a chain.
        WanFloodRules.Jump("eth4").Should().ContainInOrder("--ctstate", "NEW");
        WanFloodRules.Jump("eth4").Should().ContainInOrder("--dst-type", "LOCAL");
        string.Join(' ', WanFloodRules.ForwardJump("eth4")).Should().Contain("--ctstate NEW -m conntrack --ctstate DNAT");
    }

    [Fact]
    public void SteadyChainHasNoAttributionRules()
    {
        var steady = WanFloodRules.ChainState.Steady(true);
        WanFloodRules.Rules(false, Limits, steady).Should().NotContain(r => r.Contains("--hashlimit-name nas") || r.Contains("--hashlimit-name nap"));
        WanFloodRules.ForwardRules(false, Limits, steady).Should().NotContain(r => r.Contains("--hashlimit-name nfa"));
    }

    [Fact]
    public void NoTableIsKeyedOnSourceAndPortTogether()
    {
        // A reflection flood makes nearly every packet a new source+port key and fills such a table.
        AllRules(Suspected).Should().NotContain(r => r.Contains("--hashlimit-mode srcip,dstport"));
    }

    [Fact]
    public void EveryTableExpiresEntriesAfterTwoSeconds()
    {
        var rules = AllRules(Suspected).Where(r => r.Contains("-m hashlimit")).ToList();
        rules.Should().NotBeEmpty();
        rules.Should().OnlyContain(r => r.Contains($"--hashlimit-htable-expire {WanFloodRules.TableExpireMs}"));
        WanFloodRules.TableExpireMs.Should().Be(2000);
    }

    [Fact]
    public void SourceTablesAreSizedAboveTheGatewaysCeiling()
    {
        WanFloodRules.Rules(false, Limits, Suspected).Where(r => r.Contains("--hashlimit-mode srcip"))
            .Should().OnlyContain(r => r.Contains($"--hashlimit-htable-max {WanFloodRules.SourceTableMax}"));
        WanFloodRules.SourceTableMax.Should().Be(262144);
    }

    [Fact]
    public void DetectModeHasNoDropTargets()
    {
        AllRules(Suspected with { Enforce = false }).Should().NotContain(r => r.Contains("-j DROP"));
        AllRules(Suspected).Should().Contain(r => r.Contains("-j DROP"));
    }

    [Fact]
    public void GuardIsTheFirstRule()
    {
        WanFloodRules.Rules(false, Limits, Suspected)[0].Should().Contain(WanFloodRules.GuardV4).And.EndWith("-j RETURN");
        WanFloodRules.Rules(true, Limits, Suspected)[0].Should().Contain(WanFloodRules.GuardV6);
    }

    [Fact]
    public void FullTableFallbackRemovesEverySourceKeyedRule()
    {
        var rules = WanFloodRules.Rules(false, Limits, Suspected with { PerSource = false });
        rules.Should().NotContain(r => r.Contains("--hashlimit-mode srcip"));
        rules.Should().Contain(r => r.Contains("--hashlimit-mode dstport"));
    }

    [Fact]
    public void AttributionIsIpv4Only()
    {
        WanFloodRules.Rules(true, Limits, Suspected).Should().NotContain(r => r.Contains("--hashlimit-name nas") || r.Contains("--hashlimit-name nap"));
        WanFloodRules.Rules(true, Limits, Suspected).Where(r => r.Contains("--hashlimit-mode srcip"))
            .Should().OnlyContain(r => r.Contains("--hashlimit-srcmask 64"));
    }

    [Fact]
    public void TableNamesFitTheKernelLimitAndChangeWithTheirRate()
    {
        foreach (var name in AllRules(Suspected).SelectMany(r => r.Split(' ').SkipWhile(t => t != "--hashlimit-name").Skip(1).Take(1)))
            name.Length.Should().BeLessThanOrEqualTo(15);
        WanFloodRules.AttributionSourceTable(Limits, "udp")
            .Should().NotBe(WanFloodRules.AttributionSourceTable(Limits with { Attribution = 600 }, "udp"));
        WanFloodRules.Rules(false, Limits, Suspected).Should().Contain(r => r.Contains(WanFloodRules.AttributionSourceTable(Limits, "udp")));
        WanFloodRules.Rules(false, Limits, Suspected).Should().Contain(r => r.Contains(WanFloodRules.AttributionPortTable(Limits, "tcp")));
        WanFloodRules.ForwardRules(false, Limits, Suspected).Should().Contain(r => r.Contains(WanFloodRules.ForwardAttributionTable(Limits)));
    }

    [Fact]
    public void NeverDropCoversPortForwardsToo()
    {
        WanFloodRules.ForwardRules(false, Limits, Suspected)[0].Should().Contain(WanFloodRules.GuardV4).And.EndWith("-j RETURN");
        WanFloodRules.ForwardRules(true, Limits, Suspected)[0].Should().Contain(WanFloodRules.GuardV6).And.EndWith("-j RETURN");
    }

    [Fact]
    public void FullForwardTableFallbackRemovesThePortForwardLimit()
    {
        var paused = WanFloodRules.ForwardRules(false, Limits, Suspected with { ForwardLimits = false });
        paused.Should().NotContain(r => r.Contains("--hashlimit-name nfp_"));
        WanFloodRules.ForwardRules(false, Limits, Suspected).Should().Contain(r => r.Contains("--hashlimit-name nfp_"));
    }

    [Fact]
    public void ForwardCapsGoToTheirFamily()
    {
        WanFloodRules.ForwardRules(false, Limits, Suspected).Should().Contain(r => r.Contains("-d 192.0.2.10"));
        WanFloodRules.ForwardRules(true, Limits, Suspected).Should().NotContain(r => r.Contains("-d 192.0.2.10"));
    }

    [Fact]
    public void RestoreReplacesOneChainWithNoComments()
    {
        // UniFi's provisioner logs any commented rule it does not own as unexpected.
        var rules = WanFloodRules.Rules(false, Limits, Suspected);
        var restore = WanFloodRules.Restore(WanFloodRules.Chain, rules);
        restore.Should().StartWith("*mangle\n:NETOPT_FLOOD - [0:0]\n");
        restore.Should().NotContain("comment");
        restore.Should().EndWith("COMMIT\n");
        AllRules(Suspected).Should().NotContain(r => r.Contains("comment"));
        WanFloodRules.Fingerprint(rules).Should().NotBe(WanFloodRules.Fingerprint(WanFloodRules.Rules(false, Limits, Suspected with { Enforce = false })));
    }

    [Fact]
    public void TcFiltersNeverTouchUniFisRedirectOrSkipIt()
    {
        var drop = WanFloodRules.TcFilterAdd("eth4", WanFloodRules.DropPref(1), "203.0.113.5", "198.51.100.2", "udp", 47000, drop: true);
        var measure = WanFloodRules.TcFilterAdd("eth4", WanFloodRules.MeasurePref(1), null, "198.51.100.2", "tcp", 443, drop: false);

        string.Join(' ', drop).Should().Contain("pref 100").And.EndWith("action drop").And.Contain("match ip src 203.0.113.5/32");
        string.Join(' ', measure).Should().Contain("pref 200").And.EndWith("action continue").And.NotContain("match ip src");
        string.Join(' ', measure).Should().NotContain("pass").And.NotContain(" ok");

        var act = () => WanFloodRules.TcFilterAdd("eth4", WanFloodRules.UniFiRedirectPref, null, "198.51.100.2", "udp", 1, true);
        act.Should().Throw<ArgumentOutOfRangeException>();
        var del = () => WanFloodRules.TcFilterDel("eth4", WanFloodRules.UniFiRedirectPref);
        del.Should().Throw<ArgumentOutOfRangeException>();
        WanFloodRules.IsOurPref(WanFloodRules.DropPref(WanFloodRules.MaxSlots)).Should().BeTrue();
        WanFloodRules.IsOurPref(WanFloodRules.DropPref(WanFloodRules.MaxSlots + 1)).Should().BeFalse();
    }

    [Fact]
    public void TeardownRemovesEverythingAndOnlyOwnedQdiscs()
    {
        var script = WanFloodRules.Teardown(["eth4", "eth5"], ["eth5"]);
        script.Should().Contain("-j NETOPT_FLOOD$").And.Contain("-j NETOPT_FLOOD_FWD$");
        script.Should().Contain("-X $c").And.Contain($"ipset destroy {WanFloodRules.GuardV4}").And.Contain($"ipset destroy {WanFloodRules.GuardV6}");
        script.Should().Contain("tc filter del dev eth4 parent ffff: pref $p").And.Contain("tc filter del dev eth5 parent ffff: pref $p");
        script.Should().Contain("tc qdisc del dev eth5 ingress").And.NotContain("tc qdisc del dev eth4 ingress");
        script.Should().NotContain("49152");
    }

    [Theory]
    [InlineData("eth4", true)]
    [InlineData("eth6.100", true)]
    [InlineData("ppp0", true)]
    [InlineData("eth4; rm -rf /", false)]
    [InlineData("", false)]
    [InlineData("averyveryverylongname", false)]
    public void InterfaceNamesAreValidated(string name, bool valid) =>
        WanFloodRules.IsValidInterface(name).Should().Be(valid);
}
