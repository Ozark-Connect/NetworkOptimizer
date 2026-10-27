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
        _bus.Published.Should().ContainSingle(e => e.Title.StartsWith("Devices offline after"));
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
    public async Task ConsoleUnreachable_AlertsAndKeepsWatching()
    {
        var id = await ApplyManagementPlanAsync();
        _console.Connected = false;

        await TicksAsync(3);

        (await PlanAsync(id))!.Status.Should().Be(ConfigPlanStatus.Watching);
        _bus.Published.Should().ContainSingle(e => e.Title.StartsWith("UniFi Console unreachable"));
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
}
