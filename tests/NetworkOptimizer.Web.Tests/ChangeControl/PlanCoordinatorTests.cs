using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>The commit-confirmed watch and revert, end to end against a fake console and a real repository.</summary>
public sealed class PlanCoordinatorTests : IDisposable
{
    private const string Mac = "aa:bb:cc:00:00:01";

    private sealed class SqliteAccessor(SqliteConnection connection) : IChangeControlRepositoryAccessor
    {
        private NetworkOptimizerDbContext Context() =>
            new(new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite(connection).Options);

        public async Task<T> UseAsync<T>(Func<IChangeControlRepository, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
        {
            await using var ctx = Context();
            return await work(new ChangeControlRepository(ctx, NullLogger<ChangeControlRepository>.Instance), cancellationToken);
        }

        public async Task UseAsync(Func<IChangeControlRepository, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        {
            await using var ctx = Context();
            await work(new ChangeControlRepository(ctx, NullLogger<ChangeControlRepository>.Instance), cancellationToken);
        }
    }

    private sealed class MemoryBlobs : IConfigBlobStore
    {
        private readonly Dictionary<string, string> _blobs = [];
        private readonly Dictionary<string, byte[]> _files = [];
        public string HashOf(string content) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        public Task<string> PutAsync(string siteSlug, string content, CancellationToken cancellationToken = default) { var h = HashOf(content); _blobs[h] = content; return Task.FromResult(h); }
        public Task<string?> GetAsync(string siteSlug, string hash, CancellationToken cancellationToken = default) => Task.FromResult(_blobs.GetValueOrDefault(hash));
        public IReadOnlyList<string> ListHashes(string siteSlug) => [.. _blobs.Keys];
        public void Delete(string siteSlug, IEnumerable<string> hashes) { foreach (var h in hashes) _blobs.Remove(h); }
        public Task<string> PutFileAsync(string siteSlug, string name, byte[] content, CancellationToken cancellationToken = default) { _files[name] = content; return Task.FromResult(""); }
        public Task<byte[]?> GetFileAsync(string siteSlug, string name, CancellationToken cancellationToken = default) => Task.FromResult(_files.GetValueOrDefault(name));
        public void DeleteFile(string siteSlug, string name) => _files.Remove(name);
        public (long BlobBytes, long FileBytes) GetStorageBytes(string siteSlug) => (0, 0);
    }

    private sealed class PlainCrypto : ICredentialProtectionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string encrypted) => encrypted;
        public bool IsEncrypted(string? value) => false;
        public void EnsureKeyExists() { }
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeConsole _console;
    private readonly ChangeControlSiteEngine _engine;
    private readonly PlanCoordinator _plans;
    private readonly NetworkOptimizer.Web.Tests.Firmware.CapturingBus _bus = new();

    public PlanCoordinatorTests()
    {
        _connection.Open();
        using (var ctx = new NetworkOptimizerDbContext(new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite(_connection).Options))
            ctx.Database.EnsureCreated();
        var state = new ConfigState();
        state.Collections["networkconf"] = [new JsonObject { ["_id"] = "n1", ["name"] = "Default", ["vlan"] = 1, ["purpose"] = "corporate" }];
        state.Collections["portconf"] = [new JsonObject { ["_id"] = "p1", ["name"] = "All" }, new JsonObject { ["_id"] = "p2", ["name"] = "Cameras" }];
        state.Collections["device"] = [new JsonObject
        {
            ["_id"] = "d1", ["mac"] = Mac, ["name"] = "Switch", ["type"] = "usw", ["state"] = 1,
            ["port_overrides"] = new JsonArray(new JsonObject { ["port_idx"] = 5, ["portconf_id"] = "p1" }),
        }, new JsonObject { ["_id"] = "d2", ["mac"] = "aa:bb:cc:00:00:02", ["name"] = "AP", ["type"] = "uap", ["state"] = 1 }];
        _console = new FakeConsole(state);
        var accessor = new SqliteAccessor(_connection);
        _engine = new ChangeControlSiteEngine("default", _console, accessor, new MemoryBlobs(), NullLogger.Instance);
        _plans = new PlanCoordinator(_engine, new PlainCrypto(), NullLogger.Instance, bus: _bus);
        PlanCoordinator.ReadBackRetries = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
    }

    public void Dispose() => _connection.Dispose();

    private void DeviceState(int s) => _console.State.Get("device")[0]["state"] = s;

    /// <summary>A management-path plan (moves a port to another profile), acknowledged and applied.</summary>
    private async Task<int> ApplyManagementPlanAsync(Action<PlanDocument>? decorate = null)
    {
        var desired = _console.State.Clone();
        desired.Collections["device"][0]["port_overrides"] = new JsonArray(new JsonObject { ["port_idx"] = 5, ["portconf_id"] = "p2" });
        var plan = await _plans.CreateAsync(ConfigPlanKind.Edit, "Move port 5", "test", null, null, desired,
            new PlanOptions { Collections = new HashSet<string> { "device" } }, "Alex", CancellationToken.None, decorate);
        var (_, doc, _) = (await _plans.LoadAsync(plan.Id, CancellationToken.None))!.Value;
        doc.WatchDevices.Should().BeTrue();
        await _plans.SaveAcknowledgementsAsync(plan.Id, new PlanAcknowledgements
        {
            Items = doc.Operations.Select(o => o.Key).ToHashSet(),
            Regressions = doc.Regressions.Select(r => r.Key).ToHashSet(),
            TypedConfirmation = PlanReadiness.ConfirmationText(doc),
        }, CancellationToken.None);
        var why = await _plans.ApplyAsync(plan.Id, new PlanActor("Alex", true), requireApproval: false, automatic: false, CancellationToken.None);
        why.Should().BeNull();
        return plan.Id;
    }

    private Task<ConfigPlan?> PlanAsync(int id) => _engine.Repositories.UseAsync((r, c) => r.GetPlanAsync(id, c));

    private async Task TicksAsync(int n)
    {
        for (var i = 0; i < n; i++) await _plans.TickAsync(CancellationToken.None);
    }

    private async Task<ConfigPlan> DraftAsync(ConfigPlanKind kind, string name)
    {
        var desired = _console.State.Clone();
        desired.Collections["device"][0]["port_overrides"] = new JsonArray(new JsonObject { ["port_idx"] = 5, ["portconf_id"] = "p2" });
        return await _plans.CreateAsync(kind, name, "test", null, null, desired,
            new PlanOptions { Collections = new HashSet<string> { "device" } }, "Alex", CancellationToken.None);
    }

    [Fact]
    public async Task ABuiltAgainPlan_ReplacesTheDraftItWasBuiltOver()
    {
        var first = await DraftAsync(ConfigPlanKind.Zones, "Set up zones");
        var second = await DraftAsync(ConfigPlanKind.Zones, "Set up zones");

        await _plans.SupersedeAsync(first.Id, second, CancellationToken.None);

        (await PlanAsync(first.Id))!.Status.Should().Be(ConfigPlanStatus.Cancelled);
        (await PlanAsync(second.Id))!.Status.Should().Be(ConfigPlanStatus.Draft);
    }

    [Fact]
    public async Task ABuiltAgainPlan_LeavesASubmittedPlan_AndAnotherKind_Alone()
    {
        var submitted = await DraftAsync(ConfigPlanKind.Zones, "Set up zones");
        await _plans.SubmitAsync(submitted.Id, "Alex", CancellationToken.None);
        var otherKind = await DraftAsync(ConfigPlanKind.Edit, "Edit");
        var by = await DraftAsync(ConfigPlanKind.Zones, "Set up zones");

        await _plans.SupersedeAsync(submitted.Id, by, CancellationToken.None);
        await _plans.SupersedeAsync(otherKind.Id, by, CancellationToken.None);
        await _plans.SupersedeAsync(by.Id, by, CancellationToken.None);

        (await PlanAsync(submitted.Id))!.Status.Should().Be(ConfigPlanStatus.PendingApproval, "someone may be acting on it");
        (await PlanAsync(otherKind.Id))!.Status.Should().Be(ConfigPlanStatus.Draft);
        (await PlanAsync(by.Id))!.Status.Should().Be(ConfigPlanStatus.Draft, "a plan never replaces itself");
    }

    [Fact]
    public async Task ProvisioningDevice_DoesNotRevert()
    {
        var id = await ApplyManagementPlanAsync();
        DeviceState(5);

        await TicksAsync(3);

        (await PlanAsync(id))!.Status.Should().Be(ConfigPlanStatus.Watching);
    }

    [Fact]
    public async Task OfflineDevice_RevertsOnce_AndTheRevertIsNeverReverted()
    {
        var id = await ApplyManagementPlanAsync();
        DeviceState(0);

        await TicksAsync(2);
        (await PlanAsync(id))!.Status.Should().Be(ConfigPlanStatus.Reverted);
        _console.State.Get("device")[0]["port_overrides"]![0]!["portconf_id"]!.ToString().Should().Be("p1");

        // Another device drops after the revert: an alert, not a revert of the revert.
        _console.State.Get("device")[1]["state"] = 0;
        await TicksAsync(3);
        var plans = await _engine.Repositories.UseAsync((r, c) => r.GetPlansAsync(Enum.GetValues<ConfigPlanStatus>(), 50, c));
        plans.Should().HaveCount(2);
        plans.Single(p => p.Kind == ConfigPlanKind.Revert).Status.Should().Be(ConfigPlanStatus.Applied);
        _console.State.Get("device")[0]["port_overrides"]![0]!["portconf_id"]!.ToString().Should().Be("p1");
        _bus.Published.Should().Contain(e => e.Title.StartsWith("Problem after revert"));
    }

    [Fact]
    public async Task ConsolePathMove_OfflineDevice_AlertsOnce_NeverReverts()
    {
        var id = await ApplyManagementPlanAsync(doc => doc.Context[PlanCoordinator.ReaddressPathContext] = doc.Operations[0].Key);
        DeviceState(0);

        await TicksAsync(4);

        (await PlanAsync(id))!.Status.Should().Be(ConfigPlanStatus.Watching);
        _console.State.Get("device")[0]["port_overrides"]![0]!["portconf_id"]!.ToString().Should().Be("p2");
        _bus.Published.Should().ContainSingle(e => e.Title.StartsWith("Network devices offline after"));
    }

    [Fact]
    public async Task ManagementNetworkMove_OfflineDevice_AlertsOnce_NeverReverts()
    {
        var id = await ApplyManagementPlanAsync(doc => doc.Context[PlanCoordinator.ReaddressManagementContext] = "Management");
        DeviceState(0);

        await TicksAsync(4);

        (await PlanAsync(id))!.Status.Should().Be(ConfigPlanStatus.Watching);
        _console.State.Get("device")[0]["port_overrides"]![0]!["portconf_id"]!.ToString().Should().Be("p2");
        _bus.Published.Should().ContainSingle(e => e.Title.StartsWith("Network devices offline after")
            && e.Message.Contains("moved their management network (Management)"));
    }

    [Theory]
    [InlineData(0, true)]     // no reply: the console's old address is gone, the expected end
    [InlineData(400, false)]  // a console refusal is a failure, path write or not
    public async Task ConsolePathWrite_OnlyNoReplyCountsAsExpected(int status, bool applied)
    {
        var desired = _console.State.Clone();
        desired.Collections["device"][0]["port_overrides"] = new JsonArray(new JsonObject { ["port_idx"] = 5, ["portconf_id"] = "p2" });
        var plan = await _plans.CreateAsync(ConfigPlanKind.Edit, "Move port 5", "test", null, null, desired,
            new PlanOptions { Collections = new HashSet<string> { "device" } }, "Alex", CancellationToken.None,
            doc => doc.Context[PlanCoordinator.ReaddressPathContext] = doc.Operations.Single(o => o.ObjectId == "d1").Key);
        var (_, doc, _) = (await _plans.LoadAsync(plan.Id, CancellationToken.None))!.Value;
        await _plans.SaveAcknowledgementsAsync(plan.Id, new PlanAcknowledgements
        {
            Items = doc.Operations.Select(o => o.Key).ToHashSet(), TypedConfirmation = PlanReadiness.ConfirmationText(doc),
        }, CancellationToken.None);
        _console.Refuse["rest/device/d1"] = status;

        var why = await _plans.ApplyAsync(plan.Id, new PlanActor("Alex", true), false, false, CancellationToken.None);

        (why == null).Should().Be(applied);
        (await PlanAsync(plan.Id))!.Status.Should().Be(applied ? ConfigPlanStatus.Watching : ConfigPlanStatus.Failed);
    }

    [Fact]
    public async Task ContinueOnError_RefusalIsReported_LaterWritesRun_PlanApplies()
    {
        var desired = _console.State.Clone();
        desired.Collections["device"][0]["port_overrides"] = new JsonArray(new JsonObject { ["port_idx"] = 5, ["portconf_id"] = "p2" });
        var plan = await _plans.CreateAsync(ConfigPlanKind.Edit, "Move port 5", "test", null, null, desired,
            new PlanOptions { Collections = new HashSet<string> { "device" } }, "Alex", CancellationToken.None,
            doc => { foreach (var op in doc.Operations) op.ContinueOnError = true; });
        var (_, doc, _) = (await _plans.LoadAsync(plan.Id, CancellationToken.None))!.Value;
        doc.Operations[0].ObjectId.Should().Be("d2", "the refused write must come first for this test");
        await _plans.SaveAcknowledgementsAsync(plan.Id, new PlanAcknowledgements
        {
            Items = doc.Operations.Select(o => o.Key).ToHashSet(), TypedConfirmation = PlanReadiness.ConfirmationText(doc),
        }, CancellationToken.None);
        _console.Refuse["rest/device/d2"] = 400;

        var why = await _plans.ApplyAsync(plan.Id, new PlanActor("Alex", true), false, false, CancellationToken.None);

        why.Should().BeNull();
        var applied = (await PlanAsync(plan.Id))!;
        applied.Status.Should().Be(ConfigPlanStatus.Watching);
        applied.Message.Should().Contain("Refused by the Console: AP");
        _console.State.Get("device")[0]["port_overrides"]![0]!["portconf_id"]!.ToString().Should().Be("p2");
    }

    [Fact]
    public async Task RefusalWithoutContinueOnError_StillFailsThePlan()
    {
        var desired = _console.State.Clone();
        desired.Collections["device"][0]["port_overrides"] = new JsonArray(new JsonObject { ["port_idx"] = 5, ["portconf_id"] = "p2" });
        desired.Collections["device"][1]["name"] = "AP 2";
        var plan = await _plans.CreateAsync(ConfigPlanKind.Edit, "Move port 5", "test", null, null, desired,
            new PlanOptions { Collections = new HashSet<string> { "device" } }, "Alex", CancellationToken.None,
            doc => doc.Operations.Single(o => o.ObjectId == "d1").ContinueOnError = true);
        var (_, doc, _) = (await _plans.LoadAsync(plan.Id, CancellationToken.None))!.Value;
        await _plans.SaveAcknowledgementsAsync(plan.Id, new PlanAcknowledgements
        {
            Items = doc.Operations.Select(o => o.Key).ToHashSet(), TypedConfirmation = PlanReadiness.ConfirmationText(doc),
        }, CancellationToken.None);
        _console.Refuse["rest/device/d1"] = 400;
        _console.Refuse["rest/device/d2"] = 400;

        var why = await _plans.ApplyAsync(plan.Id, new PlanActor("Alex", true), false, false, CancellationToken.None);

        why.Should().NotBeNull();
        (await PlanAsync(plan.Id))!.Status.Should().Be(ConfigPlanStatus.Failed);
    }

    [Fact]
    public async Task ConsolePathNoReply_NamesTheWritesNeverSent()
    {
        var desired = _console.State.Clone();
        desired.Collections["networkconf"][0]["name"] = "Main";
        desired.Collections["device"][0]["port_overrides"] = new JsonArray(new JsonObject { ["port_idx"] = 5, ["portconf_id"] = "p2" });
        var plan = await _plans.CreateAsync(ConfigPlanKind.Edit, "Move", "test", null, null, desired,
            new PlanOptions { Collections = new HashSet<string> { "networkconf", "device" } }, "Alex", CancellationToken.None,
            doc => doc.Context[PlanCoordinator.ReaddressPathContext] = doc.Operations.Single(o => o.ObjectId == "n1").Key);
        var (_, doc, _) = (await _plans.LoadAsync(plan.Id, CancellationToken.None))!.Value;
        doc.Operations.FindIndex(o => o.ObjectId == "n1").Should().BeLessThan(doc.Operations.FindIndex(o => o.ObjectId == "d1"));
        await _plans.SaveAcknowledgementsAsync(plan.Id, new PlanAcknowledgements
        {
            Items = doc.Operations.Select(o => o.Key).ToHashSet(), TypedConfirmation = PlanReadiness.ConfirmationText(doc),
        }, CancellationToken.None);
        _console.Refuse["rest/networkconf/n1"] = 0;

        var why = await _plans.ApplyAsync(plan.Id, new PlanActor("Alex", true), false, false, CancellationToken.None);

        why.Should().BeNull();
        var applied = (await PlanAsync(plan.Id))!;
        applied.Message.Should().Contain("No reply for: Main").And.MatchRegex("Not sent once the Console stopped answering: [^.]*Switch");
    }

    [Fact]
    public async Task ConsoleUnreachable_AlertsAndKeepsWatching()
    {
        var id = await ApplyManagementPlanAsync();
        _console.Connected = false;

        await TicksAsync(3);

        (await PlanAsync(id))!.Status.Should().Be(ConfigPlanStatus.Watching);
        _bus.Published.Should().ContainSingle(e => e.Title.StartsWith("UniFi Console unreachable"));
    }

    [Fact]
    public async Task AZonePlan_QuietsDeviceAlerts_AsItsWritesStartAndEnd()
    {
        var quiets = 0;
        _plans.QuietDevices = () => quiets++;
        _console.State.Collections["firewall_zone"] = [new JsonObject { ["_id"] = "z-int", ["name"] = "Internal", ["zone_key"] = "internal", ["network_ids"] = new JsonArray("n1") }];
        var desired = _console.State.Clone();
        desired.Collections["firewall_zone"].Add(new JsonObject { ["_id"] = "new-zone", ["name"] = "IoT", ["network_ids"] = new JsonArray() });
        var plan = await _plans.CreateAsync(ConfigPlanKind.Edit, "Add a zone", "test", null, null, desired,
            new PlanOptions { Collections = new HashSet<string> { "firewall_zone" } }, "Alex", CancellationToken.None);
        var (_, doc, _) = (await _plans.LoadAsync(plan.Id, CancellationToken.None))!.Value;
        await _plans.SaveAcknowledgementsAsync(plan.Id, new PlanAcknowledgements
        {
            Items = doc.Operations.Select(o => o.Key).ToHashSet(), TypedConfirmation = PlanReadiness.ConfirmationText(doc),
        }, CancellationToken.None);

        (await _plans.ApplyAsync(plan.Id, new PlanActor("Alex", true), false, false, CancellationToken.None)).Should().BeNull();

        quiets.Should().Be(2);
    }

    private void ProvisioningGateway() => _console.State.Collections["device"].Add(new JsonObject
    {
        ["_id"] = "gw", ["mac"] = "aa:bb:cc:00:00:09", ["name"] = "Gateway", ["type"] = "udm", ["adopted"] = true, ["state"] = 5,
    });

    [Fact]
    public async Task APlanForTheGateway_IsNotBuiltWhileItProvisions()
    {
        ProvisioningGateway();
        _console.State.Collections["firewall_zone"] = [new JsonObject { ["_id"] = "z-int", ["name"] = "Internal", ["zone_key"] = "internal", ["network_ids"] = new JsonArray("n1") }];
        var desired = _console.State.Clone();
        desired.Collections["firewall_zone"].Add(new JsonObject { ["_id"] = "new-zone", ["name"] = "IoT", ["network_ids"] = new JsonArray() });
        var (limit, poll) = (PlanCoordinator.ProvisionWaitLimit, PlanCoordinator.ProvisionWaitPoll);
        (PlanCoordinator.ProvisionWaitLimit, PlanCoordinator.ProvisionWaitPoll) = (TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        try
        {
            var build = () => _plans.CreateAsync(ConfigPlanKind.Edit, "Add a zone", "test", null, null, desired,
                new PlanOptions { Collections = new HashSet<string> { "firewall_zone" } }, "Alex", CancellationToken.None);

            await build.Should().ThrowAsync<InvalidOperationException>().WithMessage(PlanCoordinator.StillProvisioning);
            _console.Requests.Should().NotContain(r => r.Item2.StartsWith("firewall/zone") || r.Item2.StartsWith("rest/networkconf"),
                "nothing is read for the plan until the gateway settles");

            _console.State.Get("device").Single(d => d["_id"]!.ToString() == "gw")["state"] = 1;
            (await build()).Should().NotBeNull();
        }
        finally
        {
            (PlanCoordinator.ProvisionWaitLimit, PlanCoordinator.ProvisionWaitPoll) = (limit, poll);
        }
    }

    [Fact]
    public async Task APlanThatDoesNotWriteToTheGateway_DoesNotWaitForIt()
    {
        ProvisioningGateway();

        await ApplyManagementPlanAsync();
    }

    [Fact]
    public async Task APlanWithoutZoneWrites_LeavesDeviceAlertsOn()
    {
        var quiets = 0;
        _plans.QuietDevices = () => quiets++;

        await ApplyManagementPlanAsync();

        quiets.Should().Be(0);
    }

    [Fact]
    public async Task FailedPreApplySnapshot_WritesNothing()
    {
        var desired = _console.State.Clone();
        desired.Collections["device"][0]["port_overrides"] = new JsonArray(new JsonObject { ["port_idx"] = 5, ["portconf_id"] = "p2" });
        var plan = await _plans.CreateAsync(ConfigPlanKind.Edit, "Move port 5", "test", null, null, desired,
            new PlanOptions { Collections = new HashSet<string> { "device" } }, "Alex", CancellationToken.None);
        var (_, doc, _) = (await _plans.LoadAsync(plan.Id, CancellationToken.None))!.Value;
        await _plans.SaveAcknowledgementsAsync(plan.Id, new PlanAcknowledgements
        {
            Items = doc.Operations.Select(o => o.Key).ToHashSet(), TypedConfirmation = PlanReadiness.ConfirmationText(doc),
        }, CancellationToken.None);
        _console.Refuse["rest/networkconf"] = 500;
        _console.Requests.Clear();

        var why = await _plans.ApplyAsync(plan.Id, new PlanActor("Alex", true), false, false, CancellationToken.None);

        why.Should().Contain("Nothing was written");
        // The AUDIT log search is a POST that reads.
        _console.Requests.Should().NotContain(r => r.Method != HttpMethod.Get && r.Item2 != "system-log/all");
    }

    [Fact]
    public void ChangedZones_OnlyForAZoneCreateOrDeleteThatApplied()
    {
        PlanOperation Op(string key, string collection, PlanAction action) => new() { Key = key, Collection = collection, Action = action };
        var doc = new PlanDocument { Operations = [Op("z1", "firewall_zone", PlanAction.Create), Op("z2", "firewall_zone", PlanAction.Update), Op("p1", "firewall_policy", PlanAction.Create)] };
        PlanResult Result(params (string Key, string Outcome)[] ops) => new() { Operations = ops.Select(o => new OperationResult { Key = o.Key, Outcome = o.Outcome }).ToList() };

        PlanCoordinator.ChangedZones(doc, Result(("z1", "Applied"), ("z2", "Applied"))).Should().BeTrue();
        PlanCoordinator.ChangedZones(doc, Result(("z1", "Error"), ("z2", "Applied"), ("p1", "Applied"))).Should().BeFalse("the zone create did not apply; an update adds or removes no zone");
        PlanCoordinator.ChangedZones(new PlanDocument { Operations = [Op("z3", "firewall_zone", PlanAction.Delete)] }, Result(("z3", "Applied"))).Should().BeTrue("a Roll Back deleting a zone");
    }

    [Fact]
    public async Task AfterZonesSettle_WaitsForTheGateway_ThenTellsThreatIntelligence()
    {
        var (first, poll) = (PlanCoordinator.ZoneSettleFirstWait, PlanCoordinator.ZoneSettlePoll);
        PlanCoordinator.ZoneSettleFirstWait = TimeSpan.Zero;
        PlanCoordinator.ZoneSettlePoll = TimeSpan.FromMilliseconds(10);
        try
        {
            var gateway = new System.Text.Json.Nodes.JsonObject { ["_id"] = "g1", ["mac"] = "aa:00:00:00:00:99", ["type"] = "udm", ["state"] = 5 };
            _console.State.Collections["device"] = [gateway];
            var told = new TaskCompletionSource<int>();
            var checks = 0;
            _plans.ZonesChanged = _ => { told.TrySetResult(checks); return Task.CompletedTask; };

            var waiting = _plans.AfterZonesSettleAsync(1);
            while (_console.Requests.Count(r => r.Path == "stat/device") < 2) await Task.Delay(5);
            told.Task.IsCompleted.Should().BeFalse("the gateway is still provisioning");
            gateway["state"] = 1;
            await waiting;

            told.Task.IsCompleted.Should().BeTrue();
        }
        finally
        {
            (PlanCoordinator.ZoneSettleFirstWait, PlanCoordinator.ZoneSettlePoll) = (first, poll);
            _plans.ZonesChanged = null;
        }
    }

    [Fact]
    public async Task UntickedManagementPathItem_AppliesTheRestWithoutAWatch()
    {
        var desired = _console.State.Clone();
        desired.Collections["device"][0]["port_overrides"] = new JsonArray(new JsonObject { ["port_idx"] = 5, ["portconf_id"] = "p2" });
        desired.Collections["portconf"][1]["name"] = "Camera Ports";
        var plan = await _plans.CreateAsync(ConfigPlanKind.Edit, "Port and profile", "test", null, null, desired,
            new PlanOptions { Collections = new HashSet<string> { "device", "portconf" } }, "Alex", CancellationToken.None);
        var (_, doc, _) = (await _plans.LoadAsync(plan.Id, CancellationToken.None))!.Value;
        doc.Operations.Single(o => o.Collection == "portconf").ManagementPath.Should().BeFalse();
        doc.WatchDevices.Should().BeTrue();
        await _plans.SaveAcknowledgementsAsync(plan.Id, new PlanAcknowledgements
        {
            Items = doc.Operations.Select(o => o.Key).ToHashSet(),
            Excluded = doc.Operations.Where(o => o.ManagementPath).Select(o => o.Key).ToHashSet(),
        }, CancellationToken.None);

        var why = await _plans.ApplyAsync(plan.Id, new PlanActor("Alex", true), requireApproval: false, automatic: false, CancellationToken.None);

        why.Should().BeNull();
        var applied = (await PlanAsync(plan.Id))!;
        applied.Status.Should().Be(ConfigPlanStatus.Applied, "only the profile rename applied, which needs no commit-confirmed watch");
        (await _plans.LoadAsync(plan.Id, CancellationToken.None))!.Value.Doc.WatchDevices.Should().BeFalse();
    }

    /// <summary>An Edit plan that forgets the Printer client, built, acknowledged, and applied.</summary>
    private async Task<(int PlanId, string? Why)> ForgetPrinterAsync()
    {
        _console.State.Collections["user"] =
        [
            new JsonObject { ["_id"] = "u1", ["mac"] = "aa:bb:cc:dd:ee:01", ["name"] = "Printer" },
            new JsonObject { ["_id"] = "u2", ["mac"] = "aa:bb:cc:dd:ee:02", ["name"] = "Rig" },
        ];
        var desired = _console.State.Clone();
        desired.Collections["user"].RemoveAll(u => u["_id"]!.ToString() == "u1");
        var plan = await _plans.CreateAsync(ConfigPlanKind.Edit, "Forget Printer", "test", null, null, desired,
            new PlanOptions { Collections = new HashSet<string> { "user" }, ForgetClients = true }, "Alex", CancellationToken.None);
        var (_, doc, _) = (await _plans.LoadAsync(plan.Id, CancellationToken.None))!.Value;
        await _plans.SaveAcknowledgementsAsync(plan.Id, new PlanAcknowledgements { Items = doc.Operations.Select(o => o.Key).ToHashSet() }, CancellationToken.None);
        _console.Requests.Clear();
        return (plan.Id, await _plans.ApplyAsync(plan.Id, new PlanActor("Alex", true), requireApproval: false, automatic: false, CancellationToken.None));
    }

    /// <summary>Applies a plan from the live clients to <paramref name="desired"/>, with rest/user dropping the device type as UniFi does.</summary>
    private async Task<(int PlanId, string? Why)> ApplyClientsAsync(List<JsonObject> desired)
    {
        _console.DropFields.UnionWith(["dev_id_override", "fingerprint_override"]);
        var state = _console.State.Clone();
        state.Collections["user"] = desired;
        var plan = await _plans.CreateAsync(ConfigPlanKind.Edit, "Clients", "test", null, null, state,
            new PlanOptions { Collections = new HashSet<string> { "user" } }, "Alex", CancellationToken.None);
        var (_, doc, _) = (await _plans.LoadAsync(plan.Id, CancellationToken.None))!.Value;
        await _plans.SaveAcknowledgementsAsync(plan.Id, new PlanAcknowledgements { Items = doc.Operations.Select(o => o.Key).ToHashSet() }, CancellationToken.None);
        _console.Requests.Clear();
        return (plan.Id, await _plans.ApplyAsync(plan.Id, new PlanActor("Alex", true), requireApproval: false, automatic: false, CancellationToken.None));
    }

    // dev_id_override as UniFi stores it: a string (main site's client records, 2026-11-02).
    private static JsonObject Lab(bool deviceType) => deviceType
        ? new JsonObject { ["_id"] = "u9", ["mac"] = "aa:bb:cc:dd:ee:09", ["name"] = "[Lab] Gateway", ["dev_id_override"] = "4978", ["fingerprint_override"] = true }
        : new JsonObject { ["_id"] = "u9", ["mac"] = "aa:bb:cc:dd:ee:09", ["name"] = "[Lab] Gateway" };

    [Fact]
    public async Task AClientPutBack_GetsItsDeviceType_ThroughTheStationRoute()
    {
        // A Roll Back of a forget: the client is created again, device type included.
        _console.State.Collections["user"] = [];

        var (planId, why) = await ApplyClientsAsync([Lab(deviceType: true)]);

        why.Should().BeNull();
        var call = _console.Requests.Should().ContainSingle(r => r.Path == "station/aa:bb:cc:dd:ee:09/fingerprint_override").Subject;
        call.Method.Should().Be(HttpMethod.Put);
        JsonNode.Parse(call.Body!)!["dev_id_override"]!.GetValue<int>().Should().Be(4978);
        _console.State.Get("user").Single()["dev_id_override"]!.ToString().Should().Be("4978");
        (await PlanAsync(planId))!.Status.Should().Be(ConfigPlanStatus.Applied, "the read-back finds the device type");
    }

    [Fact]
    public async Task AClientsDeviceTypeAlone_PutBack_IsSetThroughTheStationRoute()
    {
        // The client is there without its device type (a Roll Back that lost it): Return to Baseline changes only that.
        _console.State.Collections["user"] = [Lab(deviceType: false)];

        var (planId, why) = await ApplyClientsAsync([Lab(deviceType: true)]);

        why.Should().BeNull();
        _console.Requests.Should().ContainSingle(r => r.Path == "station/aa:bb:cc:dd:ee:09/fingerprint_override").Which.Method.Should().Be(HttpMethod.Put);
        _console.State.Get("user").Single()["fingerprint_override"]!.GetValue<bool>().Should().BeTrue();
        (await PlanAsync(planId))!.Status.Should().Be(ConfigPlanStatus.Applied);
    }

    [Fact]
    public async Task AClientsDeviceTypeRemoved_IsClearedThroughTheStationRoute()
    {
        _console.State.Collections["user"] = [Lab(deviceType: true)];

        var (planId, _) = await ApplyClientsAsync([Lab(deviceType: false)]);

        _console.Requests.Should().ContainSingle(r => r.Path == "station/aa:bb:cc:dd:ee:09/fingerprint_override").Which.Method.Should().Be(HttpMethod.Delete);
        _console.State.Get("user").Single().ContainsKey("dev_id_override").Should().BeFalse();
        (await PlanAsync(planId))!.Status.Should().Be(ConfigPlanStatus.Applied);
    }

    [Fact]
    public async Task AClientRename_SendsNoDeviceTypeCall()
    {
        _console.State.Collections["user"] = [Lab(deviceType: true)];
        var renamed = Lab(deviceType: true);
        renamed["name"] = "[Lab] Gateway 2";

        await ApplyClientsAsync([renamed]);

        _console.Requests.Should().NotContain(r => r.Path.StartsWith("station/"));
        _console.State.Get("user").Single()["dev_id_override"]!.ToString().Should().Be("4978");
    }

    [Fact]
    public async Task EditPlan_AConnectedClient_ThatComesBackBare_StillReadsForgotten()
    {
        _console.ForgottenClientsRejoin = true;

        var (planId, why) = await ForgetPrinterAsync();

        why.Should().BeNull();
        _console.State.Get("user").Should().Contain(u => u["_id"]!.ToString() == "u1" && u["name"] == null, "the device registered again");
        (await PlanAsync(planId))!.Status.Should().Be(ConfigPlanStatus.Applied);
    }

    [Fact]
    public async Task EditPlan_AForgetTheConsoleIgnored_IsNotApplied()
    {
        _console.ForgetIgnored = true;

        var (planId, _) = await ForgetPrinterAsync();

        (await PlanAsync(planId))!.Status.Should().NotBe(ConfigPlanStatus.Applied, "the client kept its config");
    }

    [Fact]
    public async Task EditPlan_ForgetsARemovedClient_InOneForgetCall_AndKeepsTheRest()
    {
        var (planId, why) = await ForgetPrinterAsync();
        var plan = (await PlanAsync(planId))!;

        why.Should().BeNull();
        _console.State.Get("user").Select(u => u["name"]!.ToString()).Should().Equal("Rig");
        var call = _console.Requests.Should().ContainSingle(r => r.Path == "cmd/stamgr").Subject;
        JsonNode.Parse(call.Body!)!["cmd"]!.ToString().Should().Be("forget-sta");
        JsonNode.Parse(call.Body!)!["macs"]!.AsArray().Select(m => m!.ToString()).Should().Equal("aa:bb:cc:dd:ee:01");
        (await PlanAsync(plan.Id))!.Status.Should().Be(ConfigPlanStatus.Applied);
    }

    [Fact]
    public async Task ConfigMoveRightAfterOurWrite_IsHeldUntilTheWritesGoQuiet_ThenSnapshotted()
    {
        _console.State.Get("device")[0]["cfgversion"] = "100";
        await _engine.PollNowAsync(CancellationToken.None);
        var first = await _engine.Repositories.UseAsync((r, c) => r.GetLatestSnapshotAsync(c));
        first.Should().NotBeNull("the first pass takes the initial snapshot");

        // Our write a minute ago, then someone else's rename that leaves no AUDIT entry.
        await _engine.Repositories.UseAsync((r, c) => r.AddJournalEntryAsync(new ConfigWriteJournalEntry
        {
            At = DateTime.UtcNow.AddMinutes(-1), Method = "PUT", Path = "rest/portconf/p1", Collection = "portconf", ObjectId = "p1", Status = 200,
        }, c));
        _console.State.Get("networkconf")[0]["name"] = "Main LAN";
        _console.State.Get("device")[0]["cfgversion"] = "101";
        await _engine.PollNowAsync(CancellationToken.None);

        var held = await _engine.Repositories.UseAsync((r, c) => r.GetDetectionStateAsync(c));
        held.CfgVersionsJson.Should().Contain("100|", "a move our recent write might explain is held, not stored");

        await _engine.Repositories.UseAsync((r, c) => r.PruneJournalAsync(DateTime.UtcNow, c));
        await _engine.PollNowAsync(CancellationToken.None);

        var changes = await _engine.Repositories.UseAsync((r, c) => r.GetChangesAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(1), cancellationToken: c));
        changes.Should().ContainSingle("the rename is caught once our write goes quiet");
        (await _engine.Repositories.UseAsync((r, c) => r.GetDetectionStateAsync(c))).CfgVersionsJson.Should().Contain("101|");
    }
}
