using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

/// <summary>
/// GatewayBlocklist against a fake UniFi Console that enforces what Network 11 was measured to enforce (issue #1424): a
/// referenced group cannot be deleted, a referenced group can be retyped, and any write of a policy whose group is gone or
/// of the other family is refused with api.err.FirewallIpGroupDoesNotExist.
/// </summary>
public class GatewayBlocklistConsoleTests : IDisposable
{
    private readonly FakeConsole _console = new();
    private readonly FakeSettings _settings = new();
    private readonly Mock<IThreatSystemAudit> _audit = new();
    private readonly UniFiApiClient _client;
    private readonly GatewayBlocklist _gateway;

    public GatewayBlocklistConsoleTests()
    {
        _client = new UniFiApiClient(NullLogger<UniFiApiClient>.Instance, "https://console.invalid", "", "", apiKey: "fixture-key");
        var old = (HttpClient?)Field(_client, "_httpClient");
        SetField(_client, "_httpClient", new HttpClient(_console));
        old?.Dispose();
        SetField(_client, "_isAuthenticated", true);
        var clients = new Mock<IUniFiClientAccessor>();
        clients.Setup(c => c.GetClient(It.IsAny<string?>())).Returns(_client);
        _gateway = new GatewayBlocklist(clients.Object, _settings, NullLogger<GatewayBlocklist>.Instance, _audit.Object);
    }

    public void Dispose() => _client.Dispose();

    private static readonly CancellationToken Ct = CancellationToken.None;

    private Task<GatewayBlocklistState?> State() => _gateway.LoadStateAsync(null, Ct);

    // ---------- Happy path ----------

    [Fact]
    public async Task SetUp_OnAFreshConsole_CreatesFourGroups_AndNewOnlyBlocksFromExternalToEveryOtherZone()
    {
        var result = await _gateway.SetUpAsync(null, Ct);

        result.Should().Be(new GatewaySetUpResult(12, 0, 4));
        _console.Groups.Select(g => (string)g["name"]!).Should().BeEquivalentTo(GatewayBlocklist.GroupNameV4, GatewayBlocklist.GroupNameV6,
            GatewayBlocklist.FeedsNameV4, GatewayBlocklist.FeedsNameV6);
        var ours = _console.OwnPolicies();
        ours.Should().HaveCount(12).And.OnlyContain(p => (string)p["source"]!["zone_id"]! == FakeConsole.External);
        ours.Select(p => (string)p["destination"]!["zone_id"]!).Distinct().Should().BeEquivalentTo(FakeConsole.Internal, FakeConsole.Gateway, FakeConsole.Vpn);
        ours.Should().OnlyContain(p => (string)p["connection_state_type"]! == "CUSTOM" && p["connection_states"]!.AsArray().Count == 1);
        (await State())!.Policies.Should().HaveCount(12);
        (await _gateway.VerifyAsync(null, Ct)).Should().BeNull();
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetUp_Again_AdoptsEverything_AndWritesNoGroupOrPolicy()
    {
        await _gateway.SetUpAsync(null, Ct);
        _console.Writes.Clear();

        var result = await _gateway.SetUpAsync(null, Ct);

        result.Should().Be(new GatewaySetUpResult(0, 12, 0));
        _console.Writes.Should().OnlyContain(w => w.EndsWith("batch-reorder"));
    }

    [Fact]
    public async Task Repair_OnAHealthyConsole_ReturnsFalse_AndWritesNothing()
    {
        await _gateway.SetUpAsync(null, Ct);
        await _gateway.SetFeedsOutboundAsync(null, true, Ct);
        _console.Writes.Clear();

        (await _gateway.RepairAsync(null, Ct)).Should().BeFalse();

        _console.Writes.Should().BeEmpty();
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SyncMembers_WritesEachFamily_ThenSkipsAnUnchangedList()
    {
        await _gateway.SetUpAsync(null, Ct);
        _console.Writes.Clear();

        (await _gateway.SyncMembersAsync(null, ["203.0.113.7", "2001:db8::7"], Ct)).Ok.Should().BeTrue();
        _console.Members(GatewayBlocklist.GroupNameV4).Should().Equal("203.0.113.7");
        _console.Members(GatewayBlocklist.GroupNameV6).Should().Equal("2001:db8::7");
        _console.Writes.Should().HaveCount(2);

        _console.Writes.Clear();
        (await _gateway.SyncMembersAsync(null, ["203.0.113.7", "2001:db8::7"], Ct)).Ok.Should().BeTrue();
        _console.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Narrowing_TurnsAPolicyEditedBackToAllStates_IntoNewOnly_Once()
    {
        await _gateway.SetUpAsync(null, Ct);
        var edited = _console.OwnPolicies()[0];
        edited["connection_state_type"] = "ALL";
        edited["connection_states"] = new JsonArray();

        (await _gateway.EnsureSourcePoliciesNewOnlyAsync(null, Ct)).Should().Be(1);
        var stored = _console.Policies.Single(p => (string)p["_id"]! == (string)edited["_id"]!);
        ((string)stored["connection_state_type"]!).Should().Be("CUSTOM");
        (await _gateway.EnsureSourcePoliciesNewOnlyAsync(null, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Outbound_On_CreatesAPairPerInternalZone_AndOff_RemovesThem()
    {
        await _gateway.SetUpAsync(null, Ct);

        (await _gateway.SetFeedsOutboundAsync(null, true, Ct)).Should().Be(4);
        var outbound = _console.Policies.Where(p => (string)p["name"]! == GatewayBlocklist.FeedsOutboundPolicyName).ToList();
        outbound.Should().HaveCount(4).And.OnlyContain(p => (string)p["destination"]!["zone_id"]! == FakeConsole.External);
        outbound.Select(p => (string)p["source"]!["zone_id"]!).Distinct().Should().BeEquivalentTo(FakeConsole.Internal, FakeConsole.Vpn);
        (await _gateway.VerifyAsync(null, Ct)).Should().BeNull();

        (await _gateway.SetFeedsOutboundAsync(null, false, Ct)).Should().Be(0);
        _console.Policies.Should().NotContain(p => (string)p["name"]! == GatewayBlocklist.FeedsOutboundPolicyName);
    }

    [Fact]
    public async Task Remove_DeletesOursOnly_PoliciesBeforeGroups()
    {
        _console.AddUserGroup("My Servers", "address-group");
        _console.AddUserPolicy("My Rule", FakeConsole.External, _console.GroupId("My Servers"));
        await _gateway.SetUpAsync(null, Ct);
        await _gateway.SetFeedsOutboundAsync(null, true, Ct);

        await _gateway.RemoveAsync(null, Ct);

        _console.Policies.Where(p => !(bool)p["predefined"]!).Select(p => (string)p["name"]!).Should().Equal("My Rule");
        _console.Groups.Select(g => (string)g["name"]!).Should().Equal("My Servers");
        (await State()).Should().BeNull();
    }

    // ---------- Repair ----------

    [Fact]
    public async Task ARetypedGroup_GetsItsTypeBack_KeepsEveryPolicy_AndNarrowingWorksAgain()
    {
        await _gateway.SetUpAsync(null, Ct);
        var v4 = (await State())!.GroupV4Id!;
        _console.Retype(v4, "ipv6-address-group");
        var edited = _console.OwnPolicies().First(p => (string)p["source"]!["ip_group_id"]! == v4);
        edited["connection_state_type"] = "ALL";

        // What issue #1424 logged: UniFi refuses the narrowing write while the group is the other family.
        (await _gateway.EnsureSourcePoliciesNewOnlyAsync(null, Ct)).Should().Be(0);

        (await _gateway.RepairAsync(null, Ct)).Should().BeTrue();

        _console.Group(v4)["group_type"]!.GetValue<string>().Should().Be("address-group");
        _console.OwnPolicies().Should().HaveCount(12).And.Contain(p => (string)p["_id"]! == (string)edited["_id"]!);
        // The repair's Set Up narrows it, now that UniFi accepts the write.
        _console.OwnPolicies().Should().OnlyContain(p => (string)p["connection_state_type"]! == "CUSTOM");
        (await _gateway.EnsureSourcePoliciesNewOnlyAsync(null, Ct)).Should().Be(0);
        (await _gateway.VerifyAsync(null, Ct)).Should().BeNull();
        _audit.Verify(a => a.GatewayPoliciesRepaired(null, 0, 1), Times.Once);
        (await _gateway.RepairAsync(null, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task APolicyOnAMissingGroup_IsReplaced_AndTheOthersStillNarrow()
    {
        await _gateway.SetUpAsync(null, Ct);
        var policies = _console.OwnPolicies();
        var broken = policies[0];
        broken["source"]!["ip_group_id"] = "00000000-0000-4000-8000-000000000000";
        var other = policies[1];
        other["connection_state_type"] = "ALL";

        (await _gateway.EnsureSourcePoliciesNewOnlyAsync(null, Ct)).Should().Be(1, "one broken policy no longer stops the rest");
        (await _gateway.VerifyAsync(null, Ct)).Should().Contain("name a Network List UniFi Network no longer has");

        (await _gateway.RepairAsync(null, Ct)).Should().BeTrue();

        _console.Policies.Should().NotContain(p => (string)p["_id"]! == (string)broken["_id"]!);
        _console.OwnPolicies().Should().HaveCount(12);
        (await _gateway.VerifyAsync(null, Ct)).Should().BeNull();
        _audit.Verify(a => a.GatewayPoliciesRepaired(null, 1, 0), Times.Once);
        (await _gateway.RepairAsync(null, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task AUsersOwnPolicyOnAMissingGroup_IsLeftAlone()
    {
        _console.AddUserPolicy("My Rule", FakeConsole.External, "00000000-0000-4000-8000-000000000000");
        await _gateway.SetUpAsync(null, Ct);

        (await _gateway.RepairAsync(null, Ct)).Should().BeFalse();

        _console.Policies.Should().Contain(p => (string)p["name"]! == "My Rule");
    }

    [Fact]
    public async Task UserSetUp_RepairsToo_WithoutASystemAuditEntry()
    {
        await _gateway.SetUpAsync(null, Ct);
        _console.Retype((await State())!.FeedsV4Id!, "ipv6-address-group");

        var result = await _gateway.SetUpAsync(null, Ct);

        result.Repaired.Should().Be(1);
        _console.Group((await State())!.FeedsV4Id!)["group_type"]!.GetValue<string>().Should().Be("address-group");
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnOutboundPolicyOnAMissingGroup_IsReplaced()
    {
        await _gateway.SetUpAsync(null, Ct);
        await _gateway.SetFeedsOutboundAsync(null, true, Ct);
        var broken = _console.Policies.First(p => (string)p["name"]! == GatewayBlocklist.FeedsOutboundPolicyName);
        broken["destination"]!["ip_group_id"] = "00000000-0000-4000-8000-000000000000";

        (await _gateway.RepairAsync(null, Ct)).Should().BeTrue();

        _console.Policies.Where(p => (string)p["name"]! == GatewayBlocklist.FeedsOutboundPolicyName).Should().HaveCount(4);
        _console.Policies.Should().NotContain(p => (string)p["_id"]! == (string)broken["_id"]!);
        (await _gateway.VerifyAsync(null, Ct)).Should().BeNull();
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    /// <summary>A UniFi Console's firewall groups, zones, and policies for the default site, in memory.</summary>
    private sealed class FakeConsole : HttpMessageHandler
    {
        public const string Internal = "zone-internal";
        public const string External = "zone-external";
        public const string Gateway = "zone-gateway";
        public const string Vpn = "zone-vpn";

        public List<JsonObject> Groups { get; } = [];
        public List<JsonObject> Policies { get; } = [];
        public List<string> Writes { get; } = [];
        private int _index = 10000;

        private static readonly JsonArray Zones =
        [
            Zone(Internal, "Internal", "internal"), Zone(External, "External", "external"),
            Zone(Gateway, "Gateway", "gateway"), Zone(Vpn, "Vpn", "vpn"),
        ];

        public FakeConsole()
        {
            // One of UniFi's own, which Network Optimizer must never touch.
            Policies.Add(new JsonObject
            {
                ["_id"] = "predefined-1",
                ["name"] = "Allow Return Traffic",
                ["predefined"] = true,
                ["action"] = "ALLOW",
                ["ip_version"] = "BOTH",
                ["enabled"] = true,
                ["index"] = 30000,
                ["source"] = new JsonObject { ["zone_id"] = External, ["matching_target"] = "ANY" },
                ["destination"] = new JsonObject { ["zone_id"] = Internal, ["matching_target"] = "ANY" },
            });
        }

        private static JsonObject Zone(string id, string name, string key) =>
            new() { ["_id"] = id, ["name"] = name, ["zone_key"] = key, ["network_ids"] = new JsonArray() };

        public List<JsonObject> OwnPolicies() =>
            Policies.Where(p => (string)p["name"]! is GatewayBlocklist.PolicyName or GatewayBlocklist.FeedsPolicyName).ToList();

        public JsonObject Group(string id) => Groups.Single(g => (string)g["_id"]! == id);

        public string GroupId(string name) => (string)Groups.Single(g => (string)g["name"]! == name)["_id"]!;

        public List<string> Members(string name) =>
            Groups.Single(g => (string)g["name"]! == name)["group_members"]?.AsArray().Select(m => (string)m!).ToList() ?? [];

        public void Retype(string id, string type)
        {
            Group(id)["group_type"] = type;
            Group(id).Remove("group_members");
        }

        public void AddUserGroup(string name, string type) =>
            Groups.Add(new JsonObject { ["_id"] = Guid.NewGuid().ToString(), ["name"] = name, ["group_type"] = type });

        public void AddUserPolicy(string name, string sourceZone, string groupId) => Policies.Add(new JsonObject
        {
            ["_id"] = Guid.NewGuid().ToString(),
            ["name"] = name,
            ["predefined"] = false,
            ["action"] = "BLOCK",
            ["ip_version"] = "IPV4",
            ["enabled"] = true,
            ["index"] = _index++,
            ["connection_state_type"] = "ALL",
            ["connection_states"] = new JsonArray(),
            ["source"] = new JsonObject { ["zone_id"] = sourceZone, ["matching_target"] = "IP", ["matching_target_type"] = "OBJECT", ["ip_group_id"] = groupId },
            ["destination"] = new JsonObject { ["zone_id"] = Internal, ["matching_target"] = "ANY" },
        });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method.Method;
            var body = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            if (method != "GET") Writes.Add($"{method} {path}");

            const string v1 = "/api/s/default/rest/firewallgroup";
            const string v2 = "/v2/api/site/default/firewall-policies";
            if (path == "/v2/api/site/default/firewall/zone" && method == "GET") return Json(Zones);
            if (path == v1 && method == "GET") return V1(Groups.Select(Shown));
            if (path == v1 && method == "POST")
            {
                var g = body!.AsObject();
                g["_id"] = Guid.NewGuid().ToString();
                Groups.Add(g);
                return V1([Shown(g)]);
            }
            if (path.StartsWith(v1 + "/", StringComparison.Ordinal))
            {
                var id = path[(v1.Length + 1)..];
                var g = Groups.FirstOrDefault(x => (string)x["_id"]! == id);
                if (g == null) return V1Error("api.err.IdInvalid");
                if (method == "PUT")
                {
                    foreach (var (k, v) in body!.AsObject().ToList()) g[k] = v?.DeepClone();
                    g["_id"] = id;
                    return V1([Shown(g)]);
                }
                if (method == "DELETE")
                {
                    if (Policies.Any(p => References(p, id))) return V1Error("api.err.ObjectReferredBy");
                    Groups.Remove(g);
                    return V1([]);
                }
            }
            if (path == v2 && method == "GET") return Json(new JsonArray(Policies.Select(p => (JsonNode)p.DeepClone()).ToArray()));
            if (path == v2 + "/batch-reorder" && method == "PUT") return Json(new JsonObject());
            if (path == v2 && method == "POST")
            {
                var p = body!.AsObject();
                if (Refused(p) is { } refused) return refused;
                p["_id"] = Guid.NewGuid().ToString();
                p["predefined"] = false;
                p["index"] = _index++;
                p["connection_states"] ??= new JsonArray();
                Policies.Add(p);
                return Json(p.DeepClone(), HttpStatusCode.Created);
            }
            if (path.StartsWith(v2 + "/", StringComparison.Ordinal))
            {
                var id = path[(v2.Length + 1)..];
                var i = Policies.FindIndex(x => (string)x["_id"]! == id);
                if (i < 0) return V2Error("api.err.FirewallPolicyNotFound", HttpStatusCode.NotFound);
                if (method == "DELETE")
                {
                    Policies.RemoveAt(i);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (method == "PUT")
                {
                    var p = body!.AsObject();
                    if (Refused(p) is { } refused) return refused;
                    Policies[i] = p;
                    return Json(p.DeepClone());
                }
            }
            throw new InvalidOperationException($"Unexpected console route: {method} {path}");
        }

        // Network 11 checks every group a policy names against the policy's IP version, on every write.
        private HttpResponseMessage? Refused(JsonObject policy)
        {
            var type = (string?)policy["ip_version"] switch { "IPV4" => "address-group", "IPV6" => "ipv6-address-group", _ => null };
            foreach (var side in new[] { policy["source"], policy["destination"] })
            {
                if ((string?)side?["matching_target_type"] != "OBJECT" || (string?)side["ip_group_id"] is not { Length: > 0 } id) continue;
                var g = Groups.FirstOrDefault(x => (string)x["_id"]! == id);
                if (g == null || (type != null && (string)g["group_type"]! != type))
                    return V2Error("api.err.FirewallIpGroupDoesNotExist", HttpStatusCode.BadRequest);
            }
            return null;
        }

        private static bool References(JsonObject policy, string groupId) =>
            new[] { policy["source"], policy["destination"] }.Any(s => (string?)s?["ip_group_id"] == groupId);

        // An empty group comes back without group_members, as Network 11 serves it.
        private static JsonNode Shown(JsonObject group)
        {
            var copy = group.DeepClone().AsObject();
            if (copy["group_members"] is JsonArray { Count: 0 }) copy.Remove("group_members");
            return copy;
        }

        private static HttpResponseMessage V1(IEnumerable<JsonNode> data) =>
            Json(new JsonObject { ["meta"] = new JsonObject { ["rc"] = "ok" }, ["data"] = new JsonArray(data.Select(d => d.DeepClone()).ToArray()) });

        private static HttpResponseMessage V1Error(string code) =>
            Json(new JsonObject { ["meta"] = new JsonObject { ["rc"] = "error", ["msg"] = code }, ["data"] = new JsonArray() }, HttpStatusCode.BadRequest);

        private static HttpResponseMessage V2Error(string code, HttpStatusCode status) =>
            Json(new JsonObject { ["code"] = code, ["errorCode"] = (int)status, ["message"] = code }, status);

        private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
