using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The "your ISP changed" reset for upstream discovery. A completed run whose resolved access
/// ASN differs from every committed access ASN stages a user-confirmed ISP change, which
/// supersedes per-transit off-path staging for that run. Confirming pauses every upstream
/// target for the connection (all tiers, auto and hand-added; manual targets pinned to another
/// WAN survive) and wipes the WAN's off-path evidence; declining records the new ASN so the
/// same change doesn't re-prompt.
///
/// All ASNs are RFC 5398 documentation ASNs (64496-64511) and all IPs are RFC 5737 ranges.
/// </summary>
public class UpstreamIspChangeResetTests : IDisposable
{
    private const int OldAccessAsn = 64496;
    private const int NewAccessAsn = 64497;
    private const int TransitAsn = 64498;
    private const int PathAsn = 64499;
    private const int UserAsn = 64500;

    private readonly NetworkOptimizerDbContext _db;

    public UpstreamIspChangeResetTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new NetworkOptimizerDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    // ---- Detection ----

    [Fact]
    public async Task Detects_change_when_access_asn_differs()
    {
        SeedCommittedAccess(OldAccessAsn, name: "Old Provider");
        var state = RunState(NewAccessAsn, newName: "New Provider");

        var change = await UpstreamRediscoveryService.DetectAccessIspChangeAsync(_db, "wan", state, CancellationToken.None);

        change.Should().NotBeNull();
        change!.OldAsnNumber.Should().Be(OldAccessAsn);
        change.OldAsnName.Should().Be("Old Provider");
        change.NewAsnNumber.Should().Be(NewAccessAsn);
        change.NewAsnName.Should().Be("New Provider");
        change.Confirmed.Should().BeNull();
    }

    [Fact]
    public async Task No_change_when_run_resolved_no_access_asn()
    {
        // A run that failed to attribute the access ASN must NOT read as a provider change.
        SeedCommittedAccess(OldAccessAsn);
        var state = new UpstreamTracerState
        {
            WanInterface = "wan",
            AccessHops = { Hop("192.0.2.9", asn: null) },
        };

        var change = await UpstreamRediscoveryService.DetectAccessIspChangeAsync(_db, "wan", state, CancellationToken.None);

        change.Should().BeNull();
    }

    [Fact]
    public async Task No_change_when_access_asn_unchanged()
    {
        SeedCommittedAccess(OldAccessAsn);
        var state = RunState(OldAccessAsn);

        var change = await UpstreamRediscoveryService.DetectAccessIspChangeAsync(_db, "wan", state, CancellationToken.None);

        change.Should().BeNull();
    }

    [Fact]
    public async Task No_change_when_nothing_committed_yet()
    {
        var state = RunState(NewAccessAsn);

        var change = await UpstreamRediscoveryService.DetectAccessIspChangeAsync(_db, "wan", state, CancellationToken.None);

        change.Should().BeNull();
    }

    [Fact]
    public async Task No_change_when_any_resolved_asn_overlaps_committed()
    {
        // The run sees both the stored ASN and a new one (e.g. a re-attributed middle hop):
        // the provider is still on the path, so it's not a switch.
        SeedCommittedAccess(OldAccessAsn);
        var state = new UpstreamTracerState
        {
            WanInterface = "wan",
            AccessHops = { Hop("192.0.2.10", NewAccessAsn), Hop("192.0.2.11", OldAccessAsn) },
        };

        var change = await UpstreamRediscoveryService.DetectAccessIspChangeAsync(_db, "wan", state, CancellationToken.None);

        change.Should().BeNull();
    }

    [Fact]
    public async Task No_change_when_new_asn_was_declined()
    {
        SeedCommittedAccess(OldAccessAsn);
        await UpstreamRediscoveryService.RecordDeclinedIspChangeAsync(_db, "wan", NewAccessAsn, CancellationToken.None);
        await _db.SaveChangesAsync();
        var state = RunState(NewAccessAsn);

        var change = await UpstreamRediscoveryService.DetectAccessIspChangeAsync(_db, "wan", state, CancellationToken.None);

        change.Should().BeNull();
    }

    [Fact]
    public async Task Decline_of_one_asn_does_not_suppress_a_different_one()
    {
        SeedCommittedAccess(OldAccessAsn);
        await UpstreamRediscoveryService.RecordDeclinedIspChangeAsync(_db, "wan", 64511, CancellationToken.None);
        await _db.SaveChangesAsync();
        var state = RunState(NewAccessAsn);

        var change = await UpstreamRediscoveryService.DetectAccessIspChangeAsync(_db, "wan", state, CancellationToken.None);

        change.Should().NotBeNull();
        change!.NewAsnNumber.Should().Be(NewAccessAsn);
    }

    [Fact]
    public async Task UserProvided_access_rows_are_not_the_stored_baseline()
    {
        // A hand-added access target carries no discovery evidence of the provider - with only
        // manual rows committed there's no baseline, so no change fires.
        _db.MonitoringTargets.Add(Target("192.0.2.1", MonitoringTargetType.AccessIsp, OldAccessAsn, "wan", DiscoveryMethod.UserProvided));
        await _db.SaveChangesAsync();
        var state = RunState(NewAccessAsn);

        var change = await UpstreamRediscoveryService.DetectAccessIspChangeAsync(_db, "wan", state, CancellationToken.None);

        change.Should().BeNull();
    }

    // ---- Injected-fallback baseline (no reachable first-mile router) ----

    [Fact]
    public async Task Fallback_only_baseline_with_same_asn_does_not_false_positive()
    {
        // A user whose access-ISP routers are all ICMP-silent has no DirectRouter/L2Neighbor
        // access target - only a ConfiguredFallback speedtest endpoint, stamped with _accessAsn.
        // A fresh run resolving the same access ASN (via a fallback hop again) must NOT read as
        // a provider change: detection keys on the ASN, which is identical on both sides.
        _db.MonitoringTargets.Add(Target("203.0.113.9", MonitoringTargetType.AccessIsp, OldAccessAsn, "wan",
            DiscoveryMethod.ConfiguredFallback));
        await _db.SaveChangesAsync();
        var state = new UpstreamTracerState
        {
            WanInterface = "wan",
            AccessHops = { FallbackHop("203.0.113.9", OldAccessAsn) },
        };

        var change = await UpstreamRediscoveryService.DetectAccessIspChangeAsync(_db, "wan", state, CancellationToken.None);

        change.Should().BeNull();
    }

    [Fact]
    public async Task Fallback_only_baseline_counts_as_baseline_so_a_real_change_still_fires()
    {
        // The flip side: a fallback baseline is still a baseline (only UserProvided is excluded),
        // so a genuinely different access ASN this run correctly triggers the change flow.
        _db.MonitoringTargets.Add(Target("203.0.113.9", MonitoringTargetType.AccessIsp, OldAccessAsn, "wan",
            DiscoveryMethod.ConfiguredFallback));
        await _db.SaveChangesAsync();
        var state = RunState(NewAccessAsn);

        var change = await UpstreamRediscoveryService.DetectAccessIspChangeAsync(_db, "wan", state, CancellationToken.None);

        change.Should().NotBeNull();
        change!.OldAsnNumber.Should().Be(OldAccessAsn);
        change.NewAsnNumber.Should().Be(NewAccessAsn);
    }

    // ---- Reset scope ----

    [Fact]
    public async Task Detection_counts_reset_scope_across_tiers_including_wan_agnostic_manual()
    {
        SeedCommittedAccess(OldAccessAsn);
        _db.MonitoringTargets.AddRange(
            // enabled tiers on this WAN - counted
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsn, "wan", DiscoveryMethod.DirectRouter),
            Target("203.0.113.5", MonitoringTargetType.InternetService, PathAsn, "wan", DiscoveryMethod.PathProxy),
            // hand-added, WAN-agnostic - counted and reported as manual
            Target("203.0.113.1", MonitoringTargetType.Transit, UserAsn, "", DiscoveryMethod.UserProvided),
            // hand-added but pinned to another WAN - survives the reset, not counted
            Target("203.0.113.2", MonitoringTargetType.Transit, UserAsn, "wan2", DiscoveryMethod.UserProvided),
            // other-WAN auto - not counted
            Target("198.51.100.2", MonitoringTargetType.Transit, TransitAsn, "wan2", DiscoveryMethod.DirectRouter),
            // disabled - already paused, not counted
            Target("198.51.100.3", MonitoringTargetType.Transit, TransitAsn, "wan", DiscoveryMethod.DirectRouter, enabled: false));
        await _db.SaveChangesAsync();
        var state = RunState(NewAccessAsn);

        var change = await UpstreamRediscoveryService.DetectAccessIspChangeAsync(_db, "wan", state, CancellationToken.None);

        change.Should().NotBeNull();
        // committed access row + transit + path + WAN-agnostic manual
        change!.TargetCount.Should().Be(4);
        change.ManualCount.Should().Be(1);
    }

    // ---- Supersede: no per-transit staging when an ISP change fires ----

    [Fact]
    public async Task CompletedRun_with_isp_change_stages_no_transit_removals_and_freezes_counters()
    {
        // Transit ASN sits one gated increment from confirming - a normal run would confirm it.
        SeedCommittedAccess(OldAccessAsn);
        _db.MonitoringTargets.Add(Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsn, "wan", DiscoveryMethod.DirectRouter));
        var counterJson = $"{{\"transit:as{TransitAsn}\":{{\"c\":2,\"t\":\"{DateTime.UtcNow.AddHours(-24):o}\"}}}}";
        _db.SystemSettings.Add(new SystemSetting
        {
            Key = SystemSettingKeys.UpstreamAbsentAsnCountsPrefix + "wan",
            Value = counterJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        // New access ASN, and the transit ASN absent this run.
        var state = RunState(NewAccessAsn);

        await UpstreamRediscoveryService.EvaluateCompletedRunAsync(_db, state, CancellationToken.None);

        state.IspChange.Should().NotBeNull();
        state.RemovedTransitAsns.Should().BeEmpty();
        state.PendingRemovalTransitAsns.Should().BeEmpty();
        // Counters frozen, not advanced and not cleared - a confirm wipes them, a decline
        // lets the next unchanged run resume normally.
        var row = await _db.SystemSettings.SingleAsync(
            s => s.Key == SystemSettingKeys.UpstreamAbsentAsnCountsPrefix + "wan");
        row.Value.Should().Be(counterJson);
    }

    [Fact]
    public async Task CompletedRun_without_isp_change_stages_transit_removals_normally()
    {
        SeedCommittedAccess(OldAccessAsn);
        _db.MonitoringTargets.Add(Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsn, "wan", DiscoveryMethod.DirectRouter));
        _db.SystemSettings.Add(new SystemSetting
        {
            Key = SystemSettingKeys.UpstreamAbsentAsnCountsPrefix + "wan",
            Value = $"{{\"transit:as{TransitAsn}\":{{\"c\":2,\"t\":\"{DateTime.UtcNow.AddHours(-24):o}\"}}}}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var state = RunState(OldAccessAsn); // same provider, transit ASN absent

        await UpstreamRediscoveryService.EvaluateCompletedRunAsync(_db, state, CancellationToken.None);

        state.IspChange.Should().BeNull();
        state.RemovedTransitAsns.Should().ContainSingle().Which.AsnNumber.Should().Be(TransitAsn);
    }

    // ---- Reset / decline application ----

    [Fact]
    public async Task ApplyReset_pauses_scope_and_wipes_counters_and_decline_memory()
    {
        _db.MonitoringTargets.AddRange(
            Target("192.0.2.1", MonitoringTargetType.AccessIsp, OldAccessAsn, "wan", DiscoveryMethod.DirectRouter),
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsn, "wan", DiscoveryMethod.DirectRouter),
            Target("203.0.113.5", MonitoringTargetType.InternetService, PathAsn, "wan", DiscoveryMethod.PathProxy),
            Target("203.0.113.1", MonitoringTargetType.Transit, UserAsn, "", DiscoveryMethod.UserProvided),
            // pinned to another WAN - must survive
            Target("203.0.113.2", MonitoringTargetType.Transit, UserAsn, "wan2", DiscoveryMethod.UserProvided),
            Target("198.51.100.2", MonitoringTargetType.Transit, TransitAsn, "wan2", DiscoveryMethod.DirectRouter));
        _db.SystemSettings.AddRange(
            new SystemSetting
            {
                Key = SystemSettingKeys.UpstreamAbsentAsnCountsPrefix + "wan",
                Value = $"{{\"transit:as{TransitAsn}\":3}}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new SystemSetting
            {
                Key = SystemSettingKeys.UpstreamDeclinedAccessAsnPrefix + "wan",
                Value = NewAccessAsn.ToString(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();

        var paused = await UpstreamRediscoveryService.ApplyIspChangeResetAsync(_db, "wan", CancellationToken.None);
        await _db.SaveChangesAsync();

        paused.Should().Be(4);
        var targets = await _db.MonitoringTargets.ToListAsync();
        targets.Where(t => t.WanInterface == "wan" || t.WanInterface == "")
            .Should().OnlyContain(t => !t.Enabled);
        targets.Where(t => t.WanInterface == "wan2")
            .Should().OnlyContain(t => t.Enabled);

        (await _db.SystemSettings.SingleAsync(s => s.Key == SystemSettingKeys.UpstreamAbsentAsnCountsPrefix + "wan"))
            .Value.Should().BeNull();
        (await _db.SystemSettings.SingleAsync(s => s.Key == SystemSettingKeys.UpstreamDeclinedAccessAsnPrefix + "wan"))
            .Value.Should().BeNull();
    }

    [Fact]
    public async Task RecordDecline_upserts_the_new_asn()
    {
        await UpstreamRediscoveryService.RecordDeclinedIspChangeAsync(_db, "wan", NewAccessAsn, CancellationToken.None);
        await _db.SaveChangesAsync();
        await UpstreamRediscoveryService.RecordDeclinedIspChangeAsync(_db, "wan", 64511, CancellationToken.None);
        await _db.SaveChangesAsync();

        var row = await _db.SystemSettings.SingleAsync(
            s => s.Key == SystemSettingKeys.UpstreamDeclinedAccessAsnPrefix + "wan");
        row.Value.Should().Be("64511");
    }

    // ---- helpers ----

    private void SeedCommittedAccess(int asn, string? name = null)
    {
        _db.MonitoringTargets.Add(Target("192.0.2.1", MonitoringTargetType.AccessIsp, asn, "wan",
            DiscoveryMethod.DirectRouter, asnName: name));
        _db.SaveChanges();
    }

    // Run state whose access hop resolved to `newAsn`; no transit ASNs, so any removal-eligible
    // transit ASN reads as absent this run.
    private static UpstreamTracerState RunState(int newAsn, string? newName = null) => new()
    {
        WanInterface = "wan",
        AccessHops = { Hop("192.0.2.20", newAsn, newName) },
    };

    private static AccessHopCandidate Hop(string address, int? asn, string? asnName = null) => new()
    {
        TargetId = $"access-{address}",
        Label = $"Access {address}",
        Address = address,
        AsnNumber = asn,
        AsnName = asnName,
        Method = DiscoveryMethod.DirectRouter,
    };

    // An injected curated speedtest endpoint, adopted as the access target when no first-mile
    // router answered ICMP. Carries _accessAsn just like a real access hop.
    private static AccessHopCandidate FallbackHop(string address, int? asn, string? asnName = null) => new()
    {
        TargetId = $"access-fallback-{address}",
        Label = $"Fallback {address}",
        Address = address,
        AsnNumber = asn,
        AsnName = asnName,
        Method = DiscoveryMethod.ConfiguredFallback,
    };

    private static MonitoringTarget Target(string address, MonitoringTargetType type, int? asn,
        string wan, DiscoveryMethod? method, bool enabled = true, string? asnName = null) => new()
        {
            TargetId = $"{type}-{address}",
            Name = $"{type} {address}",
            Address = address,
            TargetType = type,
            AsnNumber = asn,
            AsnName = asnName,
            WanInterface = wan,
            DiscoveryMethod = method,
            Enabled = enabled,
        };
}
