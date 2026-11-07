using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The scheduler's unanswered counter: an automatic run never pauses a target on one run's
/// reachability, but a target unanswered in three accepted runs at least 6 h apart is paused,
/// and resumes when a later run reaches it.
/// </summary>
public class UpstreamUnansweredTargetsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Hop = "198.51.100.1";
    private const string PathEnd = "203.0.113.10";

    private readonly NetworkOptimizerDbContext _db;

    public UpstreamUnansweredTargetsTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new NetworkOptimizerDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private async Task<MonitoringTarget> SeedAsync(string address, MonitoringTargetType type = MonitoringTargetType.AccessIsp,
        DiscoveryMethod method = DiscoveryMethod.DirectRouter, string wan = "wan", bool enabled = true)
    {
        var row = new MonitoringTarget
        {
            TargetId = "t-" + address + "-" + wan,
            Name = "Target " + address,
            Address = address,
            TargetType = type,
            DiscoveryMethod = method,
            WanInterface = wan,
            Enabled = enabled,
        };
        _db.MonitoringTargets.Add(row);
        await _db.SaveChangesAsync();
        return row;
    }

    private static UpstreamTracerState HopState(bool answered) => new()
    {
        WanInterface = "wan",
        AccessHops = new()
        {
            new AccessHopCandidate { TargetId = "hop", Label = "Hop", Address = Hop, Unreachable = !answered, Enabled = answered },
        },
    };

    private async Task<UpstreamRediscoveryService.UnansweredOutcome> RunAsync(UpstreamTracerState state, double hours,
        string wan = "wan", bool isUnboundRun = true)
    {
        var outcome = await UpstreamRediscoveryService.RecordUnansweredTargetsAsync(
            _db, state, wan, isUnboundRun, T0.AddHours(hours), CancellationToken.None);
        await _db.SaveChangesAsync();
        return outcome;
    }

    private async Task<bool> EnabledAsync(MonitoringTarget row) =>
        (await _db.MonitoringTargets.AsNoTracking().FirstAsync(t => t.Id == row.Id)).Enabled;

    [Fact]
    public async Task Third_miss_at_least_six_hours_apart_pauses_the_target()
    {
        var row = await SeedAsync(Hop);

        // Hourly retries inside the gate count once.
        foreach (var hours in new[] { 0, 1, 6, 7 })
            (await RunAsync(HopState(answered: false), hours)).Paused.Should().BeEmpty();
        (await EnabledAsync(row)).Should().BeTrue();

        var outcome = await RunAsync(HopState(answered: false), 12);

        outcome.Paused.Should().ContainSingle(t => t.Id == row.Id);
        (await EnabledAsync(row)).Should().BeFalse();
        (await UpstreamRediscoveryService.LoadUnansweredPendingWansAsync(_db, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_answer_resets_the_count()
    {
        var row = await SeedAsync(Hop);
        await RunAsync(HopState(answered: false), 0);
        await RunAsync(HopState(answered: false), 6);
        await RunAsync(HopState(answered: true), 12);

        (await UpstreamRediscoveryService.LoadUnansweredPendingWansAsync(_db, CancellationToken.None)).Should().BeEmpty();
        (await RunAsync(HopState(answered: false), 18)).Paused.Should().BeEmpty();
        (await RunAsync(HopState(answered: false), 24)).Paused.Should().BeEmpty();
        (await EnabledAsync(row)).Should().BeTrue();
    }

    [Fact]
    public async Task A_pending_count_marks_its_wan_for_the_recheck()
    {
        await SeedAsync(Hop);
        await RunAsync(HopState(answered: false), 0);

        (await UpstreamRediscoveryService.LoadUnansweredPendingWansAsync(_db, CancellationToken.None))
            .Should().BeEquivalentTo(new[] { "wan" });
    }

    [Fact]
    public async Task A_paused_target_resumes_when_it_answers()
    {
        var row = await SeedAsync(Hop);
        foreach (var hours in new[] { 0, 6, 12 }) await RunAsync(HopState(answered: false), hours);
        (await EnabledAsync(row)).Should().BeFalse();

        // The candidate arrives unchecked, copied from the paused row.
        var state = HopState(answered: true);
        state.AccessHops[0].Enabled = false;
        var outcome = await RunAsync(state, 18);

        outcome.Resumed.Should().ContainSingle(t => t.Id == row.Id);
        (await EnabledAsync(row)).Should().BeTrue();
        state.AccessHops[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task An_admin_resume_is_kept_until_the_target_answers()
    {
        var row = await SeedAsync(Hop);
        foreach (var hours in new[] { 0, 6, 12 }) await RunAsync(HopState(answered: false), hours);

        var tracked = await _db.MonitoringTargets.FirstAsync(t => t.Id == row.Id);
        tracked.Enabled = true;
        await _db.SaveChangesAsync();

        foreach (var hours in new[] { 18, 24, 30, 36 })
            (await RunAsync(HopState(answered: false), hours)).Paused.Should().BeEmpty();
        (await EnabledAsync(row)).Should().BeTrue();

        // Once it answers, a later outage counts again.
        await RunAsync(HopState(answered: true), 42);
        foreach (var hours in new[] { 48, 54 }) await RunAsync(HopState(answered: false), hours);
        (await RunAsync(HopState(answered: false), 60)).Paused.Should().ContainSingle();
    }

    [Fact]
    public async Task An_admin_paused_target_is_not_resumed()
    {
        var row = await SeedAsync(Hop, enabled: false);

        (await RunAsync(HopState(answered: true), 0)).Resumed.Should().BeEmpty();
        (await EnabledAsync(row)).Should().BeFalse();
    }

    [Fact]
    public async Task Hand_added_targets_are_never_counted()
    {
        var row = await SeedAsync(Hop, method: DiscoveryMethod.UserProvided);

        foreach (var hours in new[] { 0, 6, 12, 18 }) await RunAsync(HopState(answered: false), hours);

        (await EnabledAsync(row)).Should().BeTrue();
    }

    [Fact]
    public async Task Off_path_candidates_are_left_to_the_off_path_counter()
    {
        var row = await SeedAsync(Hop, type: MonitoringTargetType.Transit);
        var state = new UpstreamTracerState
        {
            WanInterface = "wan",
            TransitAsns = new()
            {
                new TransitAsnCandidate { AsnName = "Transit", HopAddress = Hop, Unreachable = true, OffPath = true },
            },
        };

        foreach (var hours in new[] { 0, 6, 12 }) (await RunAsync(state, hours)).Paused.Should().BeEmpty();
        (await EnabledAsync(row)).Should().BeTrue();
    }

    [Fact]
    public async Task Path_end_targets_count_by_their_proxy_address()
    {
        var row = await SeedAsync(PathEnd, type: MonitoringTargetType.InternetService, method: DiscoveryMethod.PathProxy);
        var state = new UpstreamTracerState
        {
            WanInterface = "wan",
            TransitAsns = new()
            {
                new TransitAsnCandidate { AsnName = "CDN", Method = DiscoveryMethod.PathProxy, PathProxyTarget = PathEnd, Unreachable = true },
            },
        };

        foreach (var hours in new[] { 0, 6, 12 }) await RunAsync(state, hours);

        (await EnabledAsync(row)).Should().BeFalse();
    }

    [Fact]
    public async Task Only_the_runs_own_wan_row_is_paused()
    {
        var own = await SeedAsync(Hop, wan: "wan2");
        var other = await SeedAsync(Hop, wan: "wan");

        foreach (var hours in new[] { 0, 6, 12 }) await RunAsync(HopState(answered: false), hours, wan: "wan2", isUnboundRun: false);

        (await EnabledAsync(own)).Should().BeFalse();
        (await EnabledAsync(other)).Should().BeTrue();
    }

    [Fact]
    public async Task A_provider_change_run_counts_nothing()
    {
        var row = await SeedAsync(Hop);
        var state = HopState(answered: false);
        state.IspChange = new IspChangeCandidate { OldAsnName = "Old", NewAsnName = "New" };

        foreach (var hours in new[] { 0, 6, 12 }) await RunAsync(state, hours);

        (await EnabledAsync(row)).Should().BeTrue();
    }

    [Fact]
    public async Task A_target_the_trace_no_longer_reaches_drops_its_count()
    {
        await SeedAsync(Hop);
        await RunAsync(HopState(answered: false), 0);

        await RunAsync(new UpstreamTracerState { WanInterface = "wan" }, 6);

        (await UpstreamRediscoveryService.LoadUnansweredPendingWansAsync(_db, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Enabled_owned_row_check_respects_the_wan_and_the_enabled_flag()
    {
        await SeedAsync(PathEnd, type: MonitoringTargetType.InternetService, method: DiscoveryMethod.PathProxy, wan: "wan2");
        await SeedAsync(Hop, enabled: false);

        (await UpstreamTracerService.HasEnabledOwnedRowAsync(_db, PathEnd, "wan2", isUnboundRun: false, CancellationToken.None)).Should().BeTrue();
        (await UpstreamTracerService.HasEnabledOwnedRowAsync(_db, PathEnd, "wan", isUnboundRun: true, CancellationToken.None)).Should().BeFalse();
        (await UpstreamTracerService.HasEnabledOwnedRowAsync(_db, Hop, "wan", isUnboundRun: true, CancellationToken.None)).Should().BeFalse();
        (await UpstreamTracerService.HasEnabledOwnedRowAsync(_db, null, "wan", isUnboundRun: true, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public void Contexts_with_a_pending_count_use_the_recheck_threshold()
    {
        var contexts = new[]
        {
            new WanContext { Id = 1, Name = "A", WanInterface = "wan2" },
            new WanContext { Id = 2, Name = "B", WanInterface = "wan3" },
        };
        var last = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
        {
            ["wan2"] = T0, ["wan3"] = T0,
        };
        var recheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wan2" };

        var due = UpstreamRediscoveryService.ContextsDueForDiscovery(contexts, last, T0.AddHours(7),
            TimeSpan.FromDays(7), recheck, TimeSpan.FromHours(6));

        due.Select(c => c.WanInterface).Should().BeEquivalentTo(new[] { "wan2" });
    }
}
