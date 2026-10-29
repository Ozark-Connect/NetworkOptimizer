using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class PlanEngineTests
{
    private const string Lan = """{"_id":"n1","name":"Default","purpose":"corporate","ip_subnet":"192.168.1.1/24","dhcpd_enabled":true}""";
    private const string Iot = """{"_id":"n2","name":"IoT","purpose":"corporate","vlan":30,"vlan_enabled":true,"ip_subnet":"192.168.30.1/24","mdns_enabled":false}""";

    private static PlanDocument Plan(ConfigState desired, ConfigState live, PlanOptions? options = null) =>
        PlanBuilder.Build(desired, live, options ?? new PlanOptions());

    [Fact]
    public void Rollback_UpdatesTheChangedField_AndTagsMdns()
    {
        var desired = State(("networkconf", Lan), ("networkconf", Iot));
        var live = State(("networkconf", Lan), ("networkconf", Iot.Replace("\"mdns_enabled\":false", "\"mdns_enabled\":true")));
        var doc = Plan(desired, live);
        var op = doc.Operations.Should().ContainSingle().Subject;
        op.Action.Should().Be(PlanAction.Update);
        op.Tier.Should().Be(PlanTiers.Acknowledge);
        op.Reasons.Should().Contain(r => r.Contains("mDNS"));
        op.Body!["mdns_enabled"]!.GetValue<bool>().Should().BeFalse();
        op.Body!["vlan"]!.GetValue<int>().Should().Be(30, "v1 updates send the whole object");
    }

    [Fact]
    public void DeletingAWlan_LeavesItsOwnApGroupToUniFi()
    {
        var desired = State(("networkconf", Lan));
        var live = State(("networkconf", Lan),
            ("apgroups", """{"_id":"g9","name":"devices_ap_group","for_wlanconf":true,"device_macs":["aa:bb:cc:dd:ee:01"]}"""),
            ("wlanconf", """{"_id":"w9","name":"Testing","networkconf_id":"n1","ap_group_ids":["g9"]}"""));
        var doc = Plan(desired, live, new PlanOptions { Collections = new HashSet<string> { "apgroups", "wlanconf" } });
        doc.Operations.Should().ContainSingle(o => o.Action == PlanAction.Delete).Which.Collection.Should().Be("wlanconf");
        doc.Notes.Should().Contain(n => n.Contains("UniFi removes it with that WLAN"));
    }

    [Fact]
    public void Create_ReferencesAnObjectTheSamePlanCreates()
    {
        var desired = State(("networkconf", Lan), ("networkconf", Iot),
            ("portconf", """{"_id":"p1","name":"IoT Port","native_networkconf_id":"n2"}"""));
        var live = State(("networkconf", Lan));
        var doc = Plan(desired, live);
        doc.Errors.Should().BeEmpty();
        var profile = doc.Operations.Single(o => o.Collection == "portconf");
        profile.Body!["native_networkconf_id"]!.GetValue<string>().Should().StartWith(PlanBuilder.RefPrefix);
        doc.Operations.IndexOf(doc.Operations.Single(o => o.Collection == "networkconf"))
            .Should().BeLessThan(doc.Operations.IndexOf(profile), "networks are written before the profiles that reference them");
    }

    [Fact]
    public void Delete_OfAReferencedObject_IsRefused_WithReferrersListed()
    {
        var desired = State(("networkconf", Lan), ("portconf", """{"_id":"p1","name":"IoT Port","native_networkconf_id":"n2"}"""));
        var live = State(("networkconf", Lan), ("networkconf", Iot), ("portconf", """{"_id":"p1","name":"IoT Port","native_networkconf_id":"n2"}"""));
        var doc = Plan(desired, live);
        doc.Errors.Should().ContainSingle(e => e.Contains("IoT Port"));
    }

    [Fact]
    public void RedactedSecret_KeepsTheCurrentValue()
    {
        var desired = State(("wlanconf", """{"_id":"w1","name":"Home","x_passphrase":"<keep current>","hide_ssid":true}"""));
        var live = State(("wlanconf", """{"_id":"w1","name":"Home","x_passphrase":"secret","hide_ssid":false}"""));
        var op = Plan(desired, live).Operations.Single();
        op.Fields.Should().NotContain(f => f.Path == "x_passphrase");
        op.Body!["x_passphrase"]!.GetValue<string>().Should().Be("secret", "the full-object PUT carries the live value");
    }

    private const string PpskLive = """{"_id":"w1","name":"Home","hide_ssid":false,"private_preshared_keys":[{"networkconf_id":"n1","password":"pw-lan"},{"networkconf_id":"n2","password":"pw-iot"}]}""";

    [Fact]
    public void RedactedPpskPasswords_AreKeptPerEntry()
    {
        var desired = State(("wlanconf", """{"_id":"w1","name":"Home","hide_ssid":false,"private_preshared_keys":[{"networkconf_id":"n1","password":"<keep current>"},{"networkconf_id":"n2","password":"<keep current>"}]}"""));
        Plan(desired, State(("wlanconf", PpskLive))).Operations.Should().BeEmpty("kept passwords restore to exactly the live entries");
    }

    [Fact]
    public void RedactedPpskPasswords_FollowTheirEntry_WhenEntriesMove()
    {
        var desired = State(("wlanconf", """{"_id":"w1","name":"Home","hide_ssid":true,"private_preshared_keys":[{"networkconf_id":"n2","password":"<keep current>"},{"networkconf_id":"n3","password":"pw-new"},{"networkconf_id":"n1","password":"<keep current>"}]}"""));
        var plan = Plan(desired, State(("wlanconf", PpskLive)));
        plan.Errors.Should().BeEmpty();
        var body = plan.Operations.Single().Body!.ToJsonString();
        body.Should().NotContain("keep current");
        var keys = plan.Operations.Single().Body!["private_preshared_keys"]!.AsArray();
        keys.Select(k => (k!["networkconf_id"]!.ToString(), k["password"]!.ToString()))
            .Should().Equal(("n2", "pw-iot"), ("n3", "pw-new"), ("n1", "pw-lan"));
    }

    [Fact]
    public void RedactedPpskPassword_WithNoMatchingEntry_IsAnError_NotAWrite()
    {
        var desired = State(("wlanconf", """{"_id":"w1","name":"Home","hide_ssid":false,"private_preshared_keys":[{"networkconf_id":"n3","password":"<keep current>"},{"networkconf_id":"n2","password":"<keep current>"}]}"""));
        var plan = Plan(desired, State(("wlanconf", PpskLive)));
        plan.Errors.Should().ContainSingle().Which.Should().Contain("matches no existing entry");
    }

    [Fact]
    public void PpskPasswordChange_ShowsAsASecretChange_WithNoValues()
    {
        var desired = State(("wlanconf", PpskLive.Replace("pw-iot", "pw-rotated")));
        var field = Plan(desired, State(("wlanconf", PpskLive))).Operations.Single().Fields.Single();
        field.Secret.Should().BeTrue();
        field.Old.Should().BeNull();
        field.New.Should().BeNull();
    }

    [Fact]
    public void NoOwnedAndGeneratedObjects_AreNeverWritten()
    {
        var desired = State(("firewallgroup", """{"_id":"g1","name":"Net. Opt. Blocklist (IPv4)","group_type":"address-group"}"""),
            ("firewall_policy", """{"_id":"p1","name":"Allow All Traffic","predefined":true,"enabled":true}"""));
        var live = State(("firewall_policy", """{"_id":"p1","name":"Allow All Traffic","predefined":true,"enabled":false}"""));
        Plan(desired, live).Operations.Should().BeEmpty();
    }

    [Fact]
    public void Lockout_UplinkPortDropsTheManagementNetwork()
    {
        var sw = """
            {"_id":"d1","mac":"aa:bb:cc:dd:ee:01","name":"Core Switch","port_overrides":[{"port_idx":9,"tagged_vlan_mgmt":"auto","excluded_networkconf_ids":[]}],
             "_no_meta":{"type":"usw"}}
            """;
        var garage = """{"_id":"d2","mac":"aa:bb:cc:dd:ee:02","name":"Garage Switch","mgmt_network_id":"n2","_no_meta":{"type":"usw","uplink":{"uplink_mac":"aa:bb:cc:dd:ee:01","uplink_remote_port":9}}}""";
        var live = State(("networkconf", Lan), ("networkconf", Iot), ("device", sw), ("device", garage));
        var desired = State(("networkconf", Lan), ("networkconf", Iot), ("device", sw.Replace("\"excluded_networkconf_ids\":[]", "\"excluded_networkconf_ids\":[\"n2\"]")), ("device", garage));
        var doc = Plan(desired, live);
        var finding = doc.Lockouts.Should().ContainSingle().Subject;
        finding.Kind.Should().Be("certain");
        finding.Devices.Should().Contain("Garage Switch");
        doc.HighestTier.Should().Be(PlanTiers.Lockout);
        doc.Operations.Single().ManagementPath.Should().BeTrue();
    }

    [Fact]
    public async Task Executor_RoundTrip_LeavesNoDifference()
    {
        var live = State(("networkconf", Lan), ("portconf", """{"_id":"p9","name":"Old"}"""));
        var desired = State(("networkconf", Lan), ("networkconf", Iot), ("portconf", """{"_id":"p1","name":"IoT Port","native_networkconf_id":"n2"}"""));
        var console = new FakeConsole(live);
        var doc = Plan(desired, console.State.Clone());
        doc.Errors.Should().BeEmpty();

        var (ok, result) = await new PlanExecutor(console, NullLogger.Instance).ApplyAsync(doc, hasGateway: true, null, CancellationToken.None);
        ok.Should().BeTrue(string.Join("; ", result.Operations.Select(o => $"{o.Key}={o.Outcome} {o.Message}")));

        var after = await new ConfigSnapshotReader(NullLogger.Instance).ReadAsync(console);
        var profile = after.Get("portconf").Single();
        profile["name"]!.GetValue<string>().Should().Be("IoT Port");
        var iot = after.Get("networkconf").Single(n => n["name"]!.GetValue<string>() == "IoT");
        profile["native_networkconf_id"]!.GetValue<string>().Should().Be(ConfigState.IdOf(iot), "the reference resolves to the new network's ID");
        PlanBuilder.Build(desired, after, new PlanOptions()).Operations.Should().BeEmpty("a second plan finds nothing left to do");
    }

    [Fact]
    public async Task Executor_RefusedButWritten_CountsAsApplied()
    {
        var console = new FakeConsole(State());
        console.RefuseButWrite.Add("firewall/zone");
        var desired = State(("firewall_zone", """{"_id":"z9","name":"IoT","network_ids":[]}"""));
        var doc = Plan(desired, console.State.Clone());
        var (ok, result) = await new PlanExecutor(console, NullLogger.Instance).ApplyAsync(doc, true, null, CancellationToken.None);
        ok.Should().BeTrue();
        result.Operations.Single().Message.Should().Contain("although UniFi answered");
    }

    [Fact]
    public async Task Executor_DroppedField_IsReportedNotApplied()
    {
        var console = new FakeConsole(State());
        console.DropFields.Add("multicast_router_networkconf_ids");
        var desired = State(("portconf", """{"_id":"p1","name":"Trunk","multicast_router_networkconf_ids":["x"]}"""));
        var doc = Plan(desired, console.State.Clone());
        var (_, result) = await new PlanExecutor(console, NullLogger.Instance).ApplyAsync(doc, true, null, CancellationToken.None);
        var r = result.Operations.Single();
        r.Outcome.Should().Be("Partial");
        r.NotApplied.Should().Contain("multicast_router_networkconf_ids");
    }

    [Fact]
    public void Carries_ExcludedNetwork_IsFalse_NativeIsTrue()
    {
        var state = State();
        var sw = Obj("""{"port_overrides":[{"port_idx":1,"native_networkconf_id":"a","tagged_vlan_mgmt":"custom","excluded_networkconf_ids":["b"]}]}""");
        LockoutChecker.Carries(state, sw, "1", "a").Should().BeTrue();
        LockoutChecker.Carries(state, sw, "1", "b").Should().BeFalse();
        LockoutChecker.Carries(state, sw, "1", "c").Should().BeTrue();
        LockoutChecker.InSubnet("192.168.30.5", "192.168.30.1/24").Should().BeTrue();
        LockoutChecker.InSubnet("192.168.31.5", "192.168.30.1/24").Should().BeFalse();
    }

    [Fact]
    public void Verify_ReferenceListToACreatedObject_ComparesItsNewId()
    {
        var desired = State(("networkconf", Lan), ("networkconf", Iot), ("portconf", """{"_id":"p0","name":"Other"}"""),
            ("portconf", """{"_id":"p1","name":"Trunk","forward":"customize","excluded_networkconf_ids":["n2"]}"""));
        var live = State(("networkconf", Lan), ("portconf", """{"_id":"p0","name":"Other"}"""));
        var doc = Plan(desired, live);
        doc.Operations.Single(o => o.Collection == "networkconf").ObjectId = "real-n2";
        doc.Operations.Single(o => o.Collection == "portconf").ObjectId = "real-p1";
        var after = State(("networkconf", Lan), ("networkconf", Iot.Replace("\"n2\"", "\"real-n2\"")),
            ("portconf", """{"_id":"p0","name":"Other"}"""),
            ("portconf", """{"_id":"real-p1","name":"Trunk","forward":"customize","excluded_networkconf_ids":["real-n2"]}"""));

        PlanCoordinator.VerifyAgainst(doc, live, after).Should().BeEmpty();

        after.Collections["portconf"][1]["excluded_networkconf_ids"] = new JsonArray("n-other");
        PlanCoordinator.VerifyAgainst(doc, live, after).Should().ContainSingle(d => d.Contains("Trunk"));
    }

    private static ConfigState MgmtSite(string mgmtKey, string sshPassword, string radiusSecret) => State(
        ("setting", $$"""{"_id":"s-mgmt","key":"mgmt","x_mgmt_key":"{{mgmtKey}}","x_ssh_password":"{{sshPassword}}","auto_upgrade":true}"""),
        ("setting", $$"""{"_id":"s-p2p","key":"peer_to_peer","ssid":"ssid-{{mgmtKey}}","psk":"psk-{{mgmtKey}}"}"""),
        ("radiusprofile", $$"""{"_id":"r1","name":"Default","attr_no_delete":true,"auth_servers":[{"ip":"192.168.1.1","port":1812,"x_secret":"{{radiusSecret}}"}]}"""));

    [Fact]
    public void OtherSite_KeepsTheTargetsGeneratedValues_CarriesSshCredentials()
    {
        var source = MgmtSite("source-key", "my-ssh-password", "source-secret");
        var target = MgmtSite("target-key", "target-ssh", "target-secret");

        var doc = Plan(source, target, new PlanOptions { SameSite = false });

        var mgmt = doc.Operations.Should().ContainSingle(o => o.Collection == "setting").Subject;
        mgmt.Body!["x_ssh_password"]!.GetValue<string>().Should().Be("my-ssh-password");
        mgmt.Body!["x_mgmt_key"]!.GetValue<string>().Should().Be("target-key", "the target's own management key is kept");
        doc.Operations.Should().NotContain(o => o.Collection == "radiusprofile");
        doc.KeptValues.Should().Equal("Device management key", "Peer-to-peer SSID and key", "Server secret on RADIUS profile Default");
        ConfigState.IdOf(source.Get("setting")[0]).Should().Be("s-mgmt");
        source.Get("setting")[0]["x_mgmt_key"]!.GetValue<string>().Should().Be("source-key", "the caller's state is not changed");
    }

    private static ConfigState IdentitySite(string id) => State(
        ("setting", $$"""{"_id":"s-magic","key":"magic_site_to_site_vpn","enabled":true,"public_key":"pub-{{id}}","x_private_key":"priv-{{id}}"}"""),
        ("setting", $$"""{"_id":"s-ident","key":"super_identity","name":"Console {{id}}"}"""),
        ("setting", $$"""{"_id":"s-fw","key":"super_fwupdate","x_sso_token":"sso-{{id}}","firmware_channel":"release"}"""),
        ("setting", $$"""{"_id":"s-ips","key":"ips","utm_token":"utm-{{id}}","ips_mode":"ips"}"""),
        ("setting", $$"""{"_id":"s-sm","key":"super_mgmt","override_inform_host":true,"override_inform_host_location":"{{id}}.example.test"}"""));

    [Fact]
    public void OtherSite_KeepsTheTargetsIdentity_SiteMagicKeysAndTokens()
    {
        var doc = Plan(IdentitySite("source"), IdentitySite("target"), new PlanOptions { SameSite = false });

        doc.Operations.Should().BeEmpty("a template carries no other site's keys, name, tokens, or inform host");
        doc.KeptValues.Should().BeEquivalentTo("Site Magic keys", "Console name", "Firmware update sign-in", "CyberSecure token", "Inform host override");
    }

    [Fact]
    public void SameSite_RestoresTheSiteMagicKeys()
    {
        var doc = Plan(IdentitySite("old"), IdentitySite("new"));

        doc.Operations.Single(o => o.ObjectName == "magic_site_to_site_vpn").Body!["public_key"]!.GetValue<string>().Should().Be("pub-old");
    }

    [Fact]
    public void OtherSite_AFieldTheSourceNeverCarried_KeepsTheTargets()
    {
        var source = State(("setting", """{"_id":"s1","key":"igmp_snooping","enabled":false}"""));
        var target = State(("setting", """{"_id":"s9","key":"igmp_snooping","enabled":false,"auto_unknown_traffic_handling":true}"""));

        Plan(source, target, new PlanOptions { SameSite = false }).Operations.Should().BeEmpty();
        Plan(source, target).Operations.Should().ContainSingle("a same-site Roll Back puts the snapshot back whole");
    }

    [Fact]
    public void SameSite_RestoresTheGeneratedValues()
    {
        var doc = Plan(MgmtSite("old-key", "pw", "old-secret"), MgmtSite("new-key", "pw", "new-secret"));

        doc.Operations.Single(o => o.ObjectName == "mgmt").Body!["x_mgmt_key"]!.GetValue<string>().Should().Be("old-key");
        doc.Operations.Should().Contain(o => o.Collection == "radiusprofile");
        doc.KeptValues.Should().BeEmpty();
    }

    [Fact]
    public void SettingNamingANewNetwork_IsWrittenAfterThatNetwork()
    {
        var desired = State(("networkconf", Lan), ("networkconf", Iot),
            ("setting", """{"_id":"s1","key":"global_nat","mode":"auto","excluded_network_ids":["n2"]}"""));
        var live = State(("networkconf", Lan),
            ("setting", """{"_id":"s1","key":"global_nat","mode":"auto","excluded_network_ids":[]}"""));

        var doc = Plan(desired, live);

        var network = doc.Operations.Single(o => o.Collection == "networkconf");
        var setting = doc.Operations.Single(o => o.Collection == "setting");
        doc.Operations.IndexOf(network).Should().BeLessThan(doc.Operations.IndexOf(setting));
        setting.Body!.ToJsonString().Should().Contain(PlanBuilder.RefPrefix + network.Key);
    }

    [Fact]
    public void DeletingANetwork_NamedOnlyInEtherlightingDefaults_IsNotBlocked()
    {
        var desired = State(("networkconf", Lan),
            ("setting", """{"_id":"s1","key":"ether_lighting","network_defaults":[{"key":"n1","raw_color_hex":"ffffff"},{"key":"n2","raw_color_hex":"0544ff"}]}"""));
        var live = State(("networkconf", Lan), ("networkconf", Iot),
            ("setting", """{"_id":"s1","key":"ether_lighting","network_defaults":[{"key":"n1","raw_color_hex":"ffffff"},{"key":"n2","raw_color_hex":"0544ff"}]}"""));

        var doc = Plan(desired, live);

        doc.Errors.Should().BeEmpty();
        doc.Operations.Should().ContainSingle(o => o.Action == PlanAction.Delete && o.ObjectId == "n2");
    }

    [Fact]
    public void WlanCreate_LeavesOutTheLegacyBandField()
    {
        const string other = """{"_id":"w0","name":"Other","networkconf_id":"n1"}""";
        var desired = State(("networkconf", Lan), ("wlanconf", other),
            ("wlanconf", """{"_id":"w1","name":"Six","networkconf_id":"n1","wlan_band":"both","wlan_bands":["2g","5g","6g"]}"""));
        var live = State(("networkconf", Lan), ("wlanconf", other));

        var create = Plan(desired, live).Operations.Single(o => o.Action == PlanAction.Create);

        create.Body!.AsObject().ContainsKey("wlan_band").Should().BeFalse();
        create.Body!["wlan_bands"]!.AsArray().Should().HaveCount(3);
    }

    [Fact]
    public void PpskWlan_PassphraseIsUniFis_NotComparedOrWritten()
    {
        const string ppsk = """{"_id":"w1","name":"Home","networkconf_id":"n1","x_passphrase":"PASS","private_preshared_keys_enabled":true,"private_preshared_keys":[{"networkconf_id":"n1","password":"key-1"}]}""";
        var live = State(("networkconf", Lan), ("wlanconf", ppsk.Replace("PASS", "generated-by-unifi")));
        var desired = State(("networkconf", Lan), ("wlanconf", ppsk.Replace("PASS", "from-another-site")));

        Plan(desired, live).Operations.Should().BeEmpty("UniFi ignores a PPSK WLAN's passphrase");

        var plain = State(("networkconf", Lan), ("wlanconf", ppsk.Replace("true", "false").Replace("PASS", "from-another-site")));
        Plan(plain, live).Operations.Should().ContainSingle("turning PPSK off makes the passphrase the WLAN's key again");
    }

    [Theory]
    [InlineData("ok", true)]
    [InlineData("error", false)]
    [InlineData("warning", false)]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void HealthReachable_UnknownIsNoSignal(string? status, bool? expected) =>
        PlanCoordinator.HealthReachable(status).Should().Be(expected);

    [Fact]
    public void Verify_SkipsAWriteThatIsWaiting()
    {
        var desired = State(("networkconf", Lan), ("networkconf", Iot));
        var live = State(("networkconf", Lan));
        var doc = Plan(desired, live);
        var key = doc.Operations.Single().Key;

        PlanCoordinator.VerifyAgainst(doc, live, live).Should().ContainSingle(d => d.Contains("not on the Console"));
        PlanCoordinator.VerifyAgainst(doc, live, live, new HashSet<string> { key }).Should().BeEmpty();
    }
}
