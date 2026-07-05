using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class MonitoringInterfaceDeploymentServiceTests
{
    // RemoveAsync/CheckStatusAsync only depend on IGatewaySshService (UniFiConnectionService is
    // only touched by PreflightAsync's UniFi-overlap gate, which these tests never exercise).
    private static MonitoringInterfaceDeploymentService BuildService(Mock<IGatewaySshService> ssh)
        => new(Mock.Of<ILogger<MonitoringInterfaceDeploymentService>>(), ssh.Object, Mock.Of<IUdmBootService>(), null!);
    private static MonitoringInterface Valid(int? vlan = null) => new()
    {
        Name = "modem0",
        WanIfName = "eth1",
        WanVlanId = vlan,
        TargetIp = "192.168.100.1",
        GatewayLocalIp = "192.168.100.2",
        SubnetPrefix = 24,
        WatchdogIntervalMinutes = 5,
    };

    [Fact]
    public void Validate_NoVlan_Ok()
        => MonitoringInterfaceDeploymentService.Validate(Valid()).Should().BeNull();

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(4094)]
    public void Validate_VlanInRange_Ok(int vlan)
        => MonitoringInterfaceDeploymentService.Validate(Valid(vlan)).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4095)]
    [InlineData(9999)]
    public void Validate_VlanOutOfRange_Rejected(int vlan)
        => MonitoringInterfaceDeploymentService.Validate(Valid(vlan)).Should().Contain("VLAN");

    [Theory]
    [InlineData("1")]
    [InlineData("192.168.100")]
    [InlineData("192.168.100.1.1")]
    public void Validate_ShorthandOrMalformedTargetIp_Rejected(string targetIp)
    {
        var mi = Valid();
        mi.TargetIp = targetIp;
        MonitoringInterfaceDeploymentService.Validate(mi).Should().Contain("Modem/ONT IP");
    }

    [Fact]
    public void BootScript_NoVlan_LeavesVlanIdEmpty()
    {
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(Valid());
        script.Should().Contain("VLAN_ID=\"\"");
        script.Should().Contain("WAN_IF=\"eth1\"");
    }

    [Fact]
    public void BootScript_WithVlan_SetsVlanIdAndRidesTheSubinterface()
    {
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(Valid(100));
        script.Should().Contain("VLAN_ID=\"100\"");
        // The subinterface is created from the physical port + VLAN id, and the macvlan
        // rides the resolved parent ($PARENT) rather than the bare port.
        script.Should().Contain("type vlan id \"$VLAN_ID\"");
        script.Should().Contain("link \"$PARENT\" type macvlan");
    }

    private static MonitoringInterface ValidAliased(int id = 1) => new()
    {
        Id = id,
        Name = "starlink0",
        WanIfName = "eth2",
        TargetIp = "192.168.100.1",
        GatewayLocalIp = "192.168.100.3",
        AliasIp = "192.168.101.1",
        SubnetPrefix = 24,
        WatchdogIntervalMinutes = 5,
    };

    [Fact]
    public void BootScript_NonAliased_UnchangedFromToday()
    {
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(Valid());
        script.Should().NotContain("ALIAS_ENABLED=\"1\"");
        script.Should().Contain("ip route replace \"$TARGET_IP/32\" dev \"$IFACE\" src \"$LOCAL_IP\" &&");
    }

    [Fact]
    public void BootScript_Aliased_ContainsMarkTableAndDnat()
    {
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(ValidAliased());
        script.Should().Contain("ALIAS_ENABLED=\"1\"");
        script.Should().Contain("ALIAS_IP=\"192.168.101.1\"");
        script.Should().Contain($"MARK=\"{MonitoringInterfaceDeploymentService.AliasMark(1)}\"");
        script.Should().Contain($"MASK=\"{MonitoringInterfaceDeploymentService.AliasMarkMask}\"");
        script.Should().Contain($"TABLE=\"{MonitoringInterfaceDeploymentService.AliasTable(1)}\"");
        script.Should().Contain("-j MARK --set-xmark");
        script.Should().Contain("-j DNAT --to-destination");
        // The script is one static template for both modes - the branch that skips the
        // main-table route when aliased is a runtime "elif" keyed on $ALIAS_ENABLED, not
        // something the generator omits from the text. So the meaningful, non-brittle
        // assertion is on that guard/branch keyword itself (mutually exclusive with the
        // alias branch above it) rather than on whether some route-add line's text appears
        // at all - it always does, in both modes, as static shell source.
        script.Should().Contain("elif ! ip route show \"$TARGET_IP/32\"");
        script.Should().Contain("src \"$LOCAL_IP\" table \"$TABLE\" && changed=1 || fail=1");
    }

    [Fact]
    public void BootScript_Aliased_IdempotencyGuardWrapsTeardownAndReadd()
    {
        // Finding: tearing down and re-adding all four alias artifacts unconditionally on
        // every watchdog tick both defeats the "only log when changed" eMMC guard AND opens a
        // sub-second window, every tick, where the mark rule is briefly absent - a window an
        // in-flight flow could use to leak toward the OTHER WAN's device via the main table,
        // the exact hijack this feature exists to prevent. The four artifacts must all be
        // checked before any of them is touched.
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(ValidAliased());

        script.Should().Contain("ip route show table \"$TABLE\"");
        script.Should().Contain("ip rule show | grep -qF \"fwmark $MARK/$MASK lookup $TABLE\"");
        script.Should().Contain("iptables -w 5 -t mangle -C PREROUTING");
        script.Should().Contain("iptables -w 5 -t nat -C PREROUTING -m mark --mark \"$MARK/$MASK\" -j DNAT");

        // The sweep must still run as part of the re-apply path (edits need it to clear a
        // stale rule keyed on the OLD alias/target IP) - the guard wraps it, not replaces it.
        script.Should().Contain("cleanup_marked_rules mangle");
        script.Should().Contain("cleanup_marked_rules nat");
    }

    [Fact]
    public void BootScript_IdempotencyGuard_AlsoChecksRouteSrc()
    {
        // A stale route with the wrong src (e.g. left over from a prior LOCAL_IP) still
        // matches "dev $IFACE" and would otherwise pass the guard unnoticed and get reported
        // as fully applied - the guard must confirm the route's src too.
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(ValidAliased());

        script.Should().Contain("ip route show table \"$TABLE\" \"$TARGET_IP/32\" 2>/dev/null | grep -q \"dev $IFACE\"");
        script.Should().Contain("ip route show table \"$TABLE\" \"$TARGET_IP/32\" 2>/dev/null | grep -q \"src $LOCAL_IP\"");
    }

    [Fact]
    public void BootScript_CleanupMarkedRules_LoopReadsFromFileNotPipe_SoFailureFlagSurvives()
    {
        // Same subshell hazard as CleanupMarkedRulesCommand (the standalone Remove-flow SSH
        // command), but in the boot script's own inline cleanup_marked_rules() function used
        // during deploy/watchdog runs: "iptables-save | grep | while ..." runs the while loop
        // in a subshell (busybox ash pipeline), so fail=1 set inside it never reaches the
        // parent - the script's final "[ $fail = 1 ] && exit 1" would stay blind to a sweep
        // failure. Fix must read from a temp file via redirection instead of piping into the
        // loop, keeping the loop (and the flag it sets) in the current shell.
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(ValidAliased());

        script.Should().NotContain("| while IFS= read -r rule");
        script.Should().Contain("done < \"$tmp\"");
        script.Should().Contain("iptables -w 5 -t \"$1\" -D $del 2>/dev/null || fail=1");
    }

    [Fact]
    public void BootScript_CriticalSteps_TrackFailureWithoutAbortingMidRun()
    {
        // A failed critical step (macvlan/address/route/mark/DNAT/SNAT) must not make the
        // script report success - but it also must not exit early, or a step that runs later
        // in the script would never get its chance to apply/repair, and cron/log bookkeeping
        // after it would silently never run either.
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(ValidAliased());

        script.Should().Contain("fail=0");
        script.Should().NotContain("set -e");

        // Every critical creation step carries the same fail-tracking suffix.
        script.Should().Contain("type macvlan mode bridge && changed=1 || fail=1");
        script.Should().Contain("noprefixroute && changed=1 || fail=1");
        script.Should().Contain("table \"$TABLE\" && changed=1 || fail=1");
        script.Should().Contain("ip rule add fwmark \"$MARK/$MASK\" lookup \"$TABLE\" && changed=1 || fail=1");
        script.Should().Contain("-j MARK --set-xmark \"$MARK/$MASK\" && changed=1 || fail=1");
        script.Should().Contain("-j DNAT --to-destination \"$TARGET_IP\" && changed=1 || fail=1");
        script.Should().Contain("-j SNAT --to-source \"$LOCAL_IP\" && changed=1 || fail=1");

        // The exit code reports the accumulated failure only at the very end, after every
        // step already ran - the last two statements in the script.
        var trimmed = script.TrimEnd('\n');
        trimmed.Should().EndWith("[ \"$fail\" = \"1\" ] && exit 1\nexit 0");
    }

    [Fact]
    public void BootScript_NonAliased_PlainRouteAlsoTracksFailure()
    {
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(Valid());
        script.Should().Contain("ip route replace \"$TARGET_IP/32\" dev \"$IFACE\" src \"$LOCAL_IP\" && changed=1 || fail=1");
    }

    [Fact]
    public void BootScript_Aliased_UsesLockWaitOnAllIptablesCalls()
    {
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(ValidAliased());
        // Every iptables invocation in the generated script must use -w 5 (lock-wait),
        // matching the existing wansteer precedent (src/wansteer/rules.go).
        foreach (var line in script.Split('\n'))
        {
            if (line.TrimStart().StartsWith("iptables ") && !line.Contains("-w 5"))
                Assert.Fail($"iptables call missing -w 5 lock-wait: {line}");
        }
    }

    [Fact]
    public void AliasMark_DerivedFromId_IsEntirelyInsideTheMaskedByte()
    {
        // The id must live INSIDE the masked byte (shifted, not added below it) - found via
        // live verification that adding the id below the mask silently discards it on SET
        // (xt_MARK only writes bits inside the mask) and can never match on the DNAT rule's
        // read (xt_mark's match compares against the raw, unmasked value). Confirm both the
        // set value and the mask leave zero bits outside the mask, so set-then-match round-trips.
        MonitoringInterfaceDeploymentService.AliasMark(1).Should().Be("0x1000000");
        MonitoringInterfaceDeploymentService.AliasMark(42).Should().Be("0x2a000000");
        MonitoringInterfaceDeploymentService.AliasMarkMask.Should().Be("0xff000000");
    }

    [Fact]
    public void AliasTable_DerivedFromId_MatchesMarkAsDecimal()
    {
        MonitoringInterfaceDeploymentService.AliasTable(1).Should().Be("16777216");
    }

    [Fact]
    public void AliasMark_MaxAliasableId_StillValid()
    {
        // 254 is the documented ceiling (MaxAliasableId) - it must keep working, unchanged.
        var act = () => MonitoringInterfaceDeploymentService.AliasMark(MonitoringInterfaceDeploymentService.MaxAliasableId);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(255)]
    [InlineData(511)] // truncates to the same masked byte as 255 - must not silently collide
    public void AliasMark_IdOutsideRange_Throws(int id)
    {
        var act = () => MonitoringInterfaceDeploymentService.AliasMark(id);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)] // shifts to table 0 (unspec) - flushing it would flush every routing table
    public void AliasTable_IdOutsideRange_Throws(int id)
    {
        var act = () => MonitoringInterfaceDeploymentService.AliasTable(id);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_AliasedInterfaceIdExceedsMax_Rejected()
    {
        var mi = ValidAliased(id: MonitoringInterfaceDeploymentService.MaxAliasableId + 1);
        MonitoringInterfaceDeploymentService.Validate(mi).Should()
            .Contain("exceeds").And.Contain(MonitoringInterfaceDeploymentService.MaxAliasableId.ToString());
    }

    [Fact]
    public void Validate_AliasedInterfaceIdAtMax_Ok()
    {
        var mi = ValidAliased(id: MonitoringInterfaceDeploymentService.MaxAliasableId);
        MonitoringInterfaceDeploymentService.Validate(mi).Should().BeNull();
    }

    [Fact]
    public void Validate_NewUnsavedAliasedInterface_IdCheckSkippedUntilSaved()
    {
        // Id is 0 (EF hasn't backfilled the real autoincrement id yet) when this runs from the
        // pre-save form validation - the id-range check must not block creating a brand-new
        // aliased interface just because its future id isn't known yet.
        var mi = ValidAliased(id: 0);
        MonitoringInterfaceDeploymentService.Validate(mi).Should().BeNull();
    }

    [Fact]
    public void MarkRangePreflightCommand_CountsTableRefsAndOnlyClassifiesForeignWhenTheyDontAllCarryOurMark()
    {
        var mark = MonitoringInterfaceDeploymentService.AliasMark(1);
        var mask = MonitoringInterfaceDeploymentService.AliasMarkMask;
        var table = MonitoringInterfaceDeploymentService.AliasTable(1);
        var cmd = MonitoringInterfaceDeploymentService.MarkRangePreflightCommand(1, "192.168.101.1", "192.168.100.1");

        // It must compare the count of all rules referencing OUR table id against the count
        // of those that ALSO carry our mark - not short-circuit to OURS on any mark match
        // anywhere in the output (the old, misclassifying behavior).
        cmd.Should().Contain($"grep -c 'lookup {table}\\b'");
        cmd.Should().Contain($"grep 'lookup {table}\\b' | grep -c '{mark}/{mask}'");
        cmd.Should().Contain("total=$(ip rule show");
        cmd.Should().Contain("ours=$(ip rule show");
        cmd.Should().Contain("if [ \"$total\" -eq 0 ] && [ \"$ipt_total\" -eq 0 ]; then echo FREE");
        cmd.Should().Contain("elif [ \"$total\" -gt 0 ] && [ \"$total\" -ne \"$ours\" ]; then echo FOREIGN");
        cmd.Should().Contain("elif [ \"$ipt_total\" -ne \"$ipt_ours\" ]; then echo FOREIGN");
        cmd.Should().Contain("else echo OURS; fi");

        // The caller keys off the literal "FOREIGN"; make sure the only classification
        // tokens emitted are the three expected ones.
        cmd.Should().Contain("echo FREE");
        cmd.Should().Contain("echo OURS");
        cmd.Should().Contain("echo FOREIGN");
    }

    [Fact]
    public void MarkRangePreflightCommand_AlsoCountsIptablesMangleAndNatRulesCarryingOurMark()
    {
        // cleanup_marked_rules() in the boot script deletes ANY mangle/nat rule carrying our
        // mark/mask, regardless of ip-rule/table usage - so Gate 4 must count those rules too,
        // not just ip rule show, or a foreign iptables-only rule would be misclassified FREE
        // and then silently deleted on first deploy.
        var mark = MonitoringInterfaceDeploymentService.AliasMark(1);
        var mask = MonitoringInterfaceDeploymentService.AliasMarkMask;
        var cmd = MonitoringInterfaceDeploymentService.MarkRangePreflightCommand(1, "192.168.101.1", "192.168.100.1");

        cmd.Should().Contain($"mangle=$(iptables-save -t mangle 2>/dev/null | grep -c -- '{mark}/{mask}')");
        cmd.Should().Contain($"nat=$(iptables-save -t nat 2>/dev/null | grep -c -- '{mark}/{mask}')");
        cmd.Should().Contain("ipt_total=$((mangle + nat))");

        // A table id with no ip-rule reference at all (total == 0) but a matching iptables rule
        // (ipt_total > 0) must be FOREIGN, not FREE - the exact case the ip-rule-only check missed.
        cmd.Should().Contain("elif [ \"$total\" -gt 0 ] && [ \"$total\" -ne \"$ours\" ]; then echo FOREIGN");
    }

    [Fact]
    public void MarkRangePreflightCommand_TableRefsAllOursButExtraForeignIptablesRule_IsForeign()
    {
        // Gate 4's iptables check must apply on EVERY branch, not just when total == 0: even
        // when our ip rule exists and every table ref carries our mark (the old "total == ours"
        // shortcut to OURS), an EXTRA mangle/nat rule carrying our mark/mask beyond our own two
        // expected rules must still classify FOREIGN - the boot script's cleanup_marked_rules()
        // sweep would otherwise delete that foreign rule on next apply.
        var mark = MonitoringInterfaceDeploymentService.AliasMark(3);
        var mask = MonitoringInterfaceDeploymentService.AliasMarkMask;
        var cmd = MonitoringInterfaceDeploymentService.MarkRangePreflightCommand(3, "192.168.101.3", "192.168.100.3");

        // Reached only when table refs are all ours (didn't hit the table-ref FOREIGN branch)
        // but ipt_total still differs from ipt_ours (an extra rule with our mark/mask exists
        // beyond the ones we'd recognize as our own).
        cmd.Should().Contain("elif [ \"$ipt_total\" -ne \"$ipt_ours\" ]; then echo FOREIGN");
        cmd.Should().Contain("ipt_ours=$((mangle_ours + nat_ours))");

        // The "ours" counts must match the EXACT canonical whole line iptables-save emits for
        // the two rules the boot script creates (grep -xF: fixed-string, full-line; note the
        // /32 iptables-save always appends to a host destination). A substring match on just
        // "-d <alias>" / "--to-destination <target>" would misclassify a foreign rule sharing
        // our mark and destination but in a different chain or with a different action - or one
        // whose destination merely extends ours textually (...100.19 contains ...100.1) - as
        // OURS, and the boot script's sweep would then delete it.
        cmd.Should().Contain(
            $"mangle_ours=$(iptables-save -t mangle 2>/dev/null | grep -cxF -- '-A PREROUTING -d 192.168.101.3/32 -j MARK --set-xmark {mark}/{mask}')");
        cmd.Should().Contain(
            $"nat_ours=$(iptables-save -t nat 2>/dev/null | grep -cxF -- '-A PREROUTING -m mark --mark {mark}/{mask} -j DNAT --to-destination 192.168.100.3')");
    }

    [Fact]
    public void MarkRangePreflightCommand_OursPatterns_MatchTheExactRulesTheBootScriptCreates()
    {
        // Lockstep guard: the canonical lines Gate 4 greps for must stay in the same argument
        // order as the rules the generated boot script actually creates (iptables-save echoes
        // the argument order of these matchers when it canonicalizes), or a legitimate re-apply
        // would start classifying its own prior deploy as FOREIGN.
        var mi = ValidAliased();
        var mark = MonitoringInterfaceDeploymentService.AliasMark(mi.Id);
        var mask = MonitoringInterfaceDeploymentService.AliasMarkMask;
        var cmd = MonitoringInterfaceDeploymentService.MarkRangePreflightCommand(mi.Id, mi.AliasIp!, mi.TargetIp);
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(mi);

        cmd.Should().Contain($"-A PREROUTING -d {mi.AliasIp}/32 -j MARK --set-xmark {mark}/{mask}");
        script.Should().Contain("iptables -w 5 -t mangle -A PREROUTING -d \"$ALIAS_IP\" -j MARK --set-xmark \"$MARK/$MASK\"");

        cmd.Should().Contain($"-A PREROUTING -m mark --mark {mark}/{mask} -j DNAT --to-destination {mi.TargetIp}");
        script.Should().Contain("iptables -w 5 -t nat -A PREROUTING -m mark --mark \"$MARK/$MASK\" -j DNAT --to-destination \"$TARGET_IP\"");
    }

    [Fact]
    public void CleanupMarkedRulesCommand_LoopReadsFromFileNotPipe_SoFailureFlagSurvives()
    {
        // A pipeline's exit status is its LAST command's - piping "iptables-save | grep"
        // straight into a while loop that deletes rules would report whether the loop ran,
        // never whether an individual deletion inside it failed. Piping INTO a while loop
        // also runs its body in a subshell, so a fail flag set inside it would vanish the
        // instant the pipe finishes. Assert the fix's actual shape: candidates land in a
        // temp file first, and the while loop reads it via "<" redirection (not a pipe),
        // which keeps the loop - and the flag it sets - in the current shell.
        var cmd = MonitoringInterfaceDeploymentService.CleanupMarkedRulesCommand("mangle", "0x1000000", "starlink0");

        cmd.Should().Contain("> \"$tmp\"");
        cmd.Should().Contain("done < \"$tmp\"");
        cmd.Should().NotContain("| while");
        cmd.Should().Contain("fail=0");
        cmd.Should().Contain("iptables -w 5 -t mangle -D $del 2>/dev/null || fail=1");
        cmd.Should().Contain("[ \"$fail\" = \"1\" ] && exit 1; exit 0");
    }

    [Fact]
    public void CleanupMarkedRulesCommand_ParameterizesTheGivenTable()
    {
        var mangle = MonitoringInterfaceDeploymentService.CleanupMarkedRulesCommand("mangle", "0x1000000", "starlink0");
        var nat = MonitoringInterfaceDeploymentService.CleanupMarkedRulesCommand("nat", "0x1000000", "starlink0");

        mangle.Should().Contain("iptables-save -t mangle");
        mangle.Should().Contain("iptables -w 5 -t mangle -D");
        nat.Should().Contain("iptables-save -t nat");
        nat.Should().Contain("iptables -w 5 -t nat -D");
    }

    [Fact]
    public void IsFullyApplied_AliasedInterface_RequiresMarkTableAndDnat()
    {
        var mi = ValidAliased();
        var status = new MonitoringInterfaceDeploymentService.InterfaceStatus
        {
            InterfaceExists = true,
            LocalIpAssigned = true,
            RoutePresent = true,
            SnatPresent = true,
            WatchdogCronPresent = true,
            BootScriptPresent = true,
            // Alias-specific fields left false/default.
        };

        status.IsFullyApplied(mi).Should().BeFalse("mark/policy-route/DNAT aren't confirmed yet");

        status.MarkRulePresent = true;
        status.PolicyRoutePresent = true;
        status.DnatRulePresent = true;
        status.IsFullyApplied(mi).Should().BeTrue();
    }

    [Fact]
    public void IsFullyApplied_NonAliasedInterface_UnaffectedByNewAliasFields()
    {
        var mi = Valid();
        var status = new MonitoringInterfaceDeploymentService.InterfaceStatus
        {
            InterfaceExists = true,
            LocalIpAssigned = true,
            RoutePresent = true,
            SnatPresent = true,
            WatchdogCronPresent = true,
            BootScriptPresent = true,
        };

        status.IsFullyApplied(mi).Should().BeTrue("non-alias behavior must be unchanged");
    }

    [Fact]
    public void Validate_AliasedWithoutSnat_Rejected()
    {
        var mi = ValidAliased();
        mi.SnatEnabled = false;
        MonitoringInterfaceDeploymentService.Validate(mi).Should().Contain("SNAT");
    }

    [Fact]
    public void Validate_AliasEqualsTarget_Rejected()
    {
        var mi = ValidAliased();
        mi.AliasIp = mi.TargetIp;
        MonitoringInterfaceDeploymentService.Validate(mi).Should().Contain("Alias IP");
    }

    [Fact]
    public void Validate_AliasEqualsGatewayLocalIp_Rejected()
    {
        var mi = ValidAliased();
        mi.AliasIp = mi.GatewayLocalIp;
        MonitoringInterfaceDeploymentService.Validate(mi).Should().Contain("Alias IP");
    }

    [Fact]
    public void Validate_AliasInsideTargetSubnet_Rejected()
    {
        var mi = ValidAliased();
        mi.AliasIp = "192.168.100.50"; // inside 192.168.100.0/24, same as TargetIp's subnet
        MonitoringInterfaceDeploymentService.Validate(mi).Should().Contain("Alias IP");
    }

    [Fact]
    public void Validate_AliasOutsideTargetSubnet_Ok()
    {
        var mi = ValidAliased(); // AliasIp = 192.168.101.1, TargetIp subnet = 192.168.100.0/24
        MonitoringInterfaceDeploymentService.Validate(mi).Should().BeNull();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("192.168.101")]
    public void Validate_ShorthandAliasIp_Rejected(string aliasIp)
    {
        var mi = ValidAliased();
        mi.AliasIp = aliasIp;
        MonitoringInterfaceDeploymentService.Validate(mi).Should().Contain("Alias IP");
    }

    [Fact]
    public void Validate_NoAlias_AliasValidationSkipped()
    {
        // AliasIp is null on the default Valid() fixture - none of the new alias rules apply.
        MonitoringInterfaceDeploymentService.Validate(Valid()).Should().BeNull();
    }

    [Fact]
    public void BootScript_AddressAssignment_UsesNoprefixroute()
    {
        // Found via live verification on real hardware: without noprefixroute, `ip addr add
        // x.x.x.x/24` auto-installs a connected route for the WHOLE subnet in the main table,
        // silently hijacking every other address in it - including a second WAN's device
        // sharing the same target IP, defeating the entire point of alias mode (and, for a
        // plain interface, quietly claiming reachability to addresses the user never asked for).
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(Valid());
        script.Should().Contain("ip addr add \"$LOCAL_IP/$PREFIX\" dev \"$IFACE\" noprefixroute");
    }

    [Fact]
    public void BootScript_AddressIdempotencyCheck_RequiresNoprefixrouteFlag()
    {
        // The idempotency check must look for the noprefixroute flag specifically, not just
        // the address - otherwise an interface deployed before this fix would keep looking
        // "already applied" on every watchdog run and never get migrated off its phantom route.
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(Valid());
        script.Should().Contain("grep \"inet $LOCAL_IP/$PREFIX \" | grep -q noprefixroute");
    }

    private static NetworkInfo UniFiNet(string name, string subnet, bool wan = false, bool enabled = true) => new()
    {
        Name = name,
        Purpose = wan ? "wan" : "corporate",
        Enabled = enabled,
        IpSubnet = subnet,
    };

    [Fact]
    public void CheckUniFiNetworkOverlap_AliasInsideUniFiNetwork_BlockedWithAliasSpecificReason()
    {
        // AliasIp landing inside a real UniFi LAN installs a mangle mark + DNAT rule that
        // hijacks that network's traffic - the fix is a different alias, not renumbering the
        // device, so the block reason must say so (distinct from the TargetIp overlap message).
        var mi = ValidAliased();
        mi.AliasIp = "10.10.10.5";
        var networks = new List<NetworkInfo> { UniFiNet("Home LAN", "10.10.10.0/24") };

        var result = MonitoringInterfaceDeploymentService.CheckUniFiNetworkOverlap(mi, networks);

        result.Should().NotBeNull();
        result!.Block.Should().Be(MonitoringInterfaceDeploymentService.PreflightBlock.UniFiOverlap);
        result.Message.Should().Contain("Alias IP").And.Contain("Home LAN");
    }

    [Fact]
    public void CheckUniFiNetworkOverlap_AliasOutsideUniFiNetworks_PassesGate1()
    {
        var mi = ValidAliased(); // AliasIp = 192.168.101.1
        var networks = new List<NetworkInfo> { UniFiNet("Home LAN", "10.10.10.0/24") };

        MonitoringInterfaceDeploymentService.CheckUniFiNetworkOverlap(mi, networks).Should().BeNull();
    }

    [Fact]
    public void CheckUniFiNetworkOverlap_TargetIpInsideUniFiNetwork_BlockedWithTargetSpecificReason()
    {
        // Regression guard: the pre-existing TargetIp/GatewayLocalIp overlap check (Gate 1
        // before this fix) must keep working unchanged alongside the new AliasIp check.
        var mi = Valid(); // TargetIp = 192.168.100.1
        var networks = new List<NetworkInfo> { UniFiNet("Home LAN", "192.168.100.0/24") };

        var result = MonitoringInterfaceDeploymentService.CheckUniFiNetworkOverlap(mi, networks);

        result.Should().NotBeNull();
        result!.Message.Should().Contain(mi.TargetIp).And.NotContain("Alias IP");
    }

    [Fact]
    public async Task RemoveAsync_AliasedRowWithOutOfRangeId_SkipsAliasCleanupAndSucceeds()
    {
        // A SAVED aliased row's Id is assigned by SQLite at insert and can exceed
        // MaxAliasableId (Validate only gates deploy, not persistence) - Remove must not call
        // AliasMark/AliasTable for it (they throw for out-of-range ids) or the row becomes
        // permanently stuck in the UI with no way to delete it.
        var mi = ValidAliased(id: MonitoringInterfaceDeploymentService.MaxAliasableId + 37);
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, ""));
        var service = BuildService(ssh);

        var (success, steps) = await service.RemoveAsync(mi);

        success.Should().BeTrue();
        steps.Should().Contain(s => s.Contains("outside the aliasable range"));
        steps.Should().NotContain(s => s.Contains("WARNING"));
        steps.Should().NotContain(s => s.Contains("Removed alias mark/DNAT rules"));
    }

    [Fact]
    public async Task CheckStatusAsync_AliasedRowWithOutOfRangeId_ReportsAliasFlagsAbsentWithoutThrowing()
    {
        var mi = ValidAliased(id: MonitoringInterfaceDeploymentService.MaxAliasableId + 1);
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true,
                "---UDM_BOOT---\ny\n---IFACE---\ny\n---LOCALIP---\ny\n---ROUTE---\ny\n---SNAT---\ny\n" +
                "---CRON---\ny\n---SCRIPT---\ny"));
        var service = BuildService(ssh);

        var status = await service.CheckStatusAsync(mi);

        status.GatewayReachable.Should().BeTrue();
        status.PolicyRoutePresent.Should().BeFalse();
        status.MarkRulePresent.Should().BeFalse();
        status.DnatRulePresent.Should().BeFalse();
        status.IsFullyApplied(mi).Should().BeFalse("an out-of-range alias id can never have valid gateway alias artifacts");
    }

    [Fact]
    public async Task CheckStatusAsync_Aliased_PolicyRouteProbeChecksBothDevAndSrc()
    {
        // Finding: a stale route with the wrong src (e.g. left from a prior LOCAL_IP) still
        // matches "dev $IFACE" alone and would be reported as present - the probe must also
        // confirm the route's src.
        var mi = ValidAliased(id: 9);
        string? capturedCommand = null;
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan?, CancellationToken>((cmd, _, _) => capturedCommand = cmd)
            .ReturnsAsync((true, ""));
        var service = BuildService(ssh);

        await service.CheckStatusAsync(mi);

        capturedCommand.Should().NotBeNull();
        capturedCommand!.Should().Contain($"grep -q 'dev {mi.Name}'");
        capturedCommand.Should().Contain($"grep -q 'src {mi.GatewayLocalIp}'");
    }
}
