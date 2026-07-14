using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Change-detection for the scheduled upstream re-discovery. Keys on a stable upstream-ASN
/// identity (ECMP-proof), flags ADDED ASNs immediately, and gates REMOVED behind a consecutive-
/// miss counter so incomplete/degraded runs don't nag. UserProvided (hand-added) targets suppress
/// "added" and never make an ASN removal-eligible on their own - but once an auto-discovered
/// sibling proves the ASN was on the path, a confirmed removal pauses hand-added targets too.
///
/// All ASNs are RFC 5398 documentation ASNs (64496-64511) and all IPs are RFC 5737 ranges.
/// </summary>
public class UpstreamRediscoverySignatureTests : IDisposable
{
    private const int AccessAsn = 64496;
    private const int TransitAsnA = 64497;
    private const int TransitAsnB = 64498;
    private const int PathAsn = 64499;
    private const int UserAsn = 64500;

    private readonly NetworkOptimizerDbContext _db;

    public UpstreamRediscoverySignatureTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new NetworkOptimizerDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    // ---- IdentityKey ----

    [Theory]
    [InlineData(MonitoringTargetType.AccessIsp, 64497, "access:as64497")]
    [InlineData(MonitoringTargetType.Transit, 64497, "transit:as64497")]
    [InlineData(MonitoringTargetType.InternetService, 64497, "path:as64497")]
    public void IdentityKey_namespaces_by_tier(MonitoringTargetType type, int asn, string expected)
    {
        UpstreamRediscoveryService.IdentityKey(type, asn, "192.0.2.1").Should().Be(expected);
    }

    [Fact]
    public void IdentityKey_falls_back_to_address_when_no_asn()
    {
        UpstreamRediscoveryService.IdentityKey(MonitoringTargetType.AccessIsp, null, "192.0.2.9")
            .Should().Be("access:192.0.2.9");
    }

    [Fact]
    public void IdentityKey_same_asn_different_ip_is_identical()
    {
        var a = UpstreamRediscoveryService.IdentityKey(MonitoringTargetType.Transit, TransitAsnA, "192.0.2.1");
        var b = UpstreamRediscoveryService.IdentityKey(MonitoringTargetType.Transit, TransitAsnA, "198.51.100.7");
        a.Should().Be(b);
    }

    // ---- Candidate signature (reachability-independent) ----

    [Fact]
    public void Candidate_collapses_ecmp_hops_within_an_asn()
    {
        var state = new UpstreamTracerState
        {
            AccessHops = { Hop("192.0.2.1", AccessAsn) },
            TransitAsns =
            {
                Transit("198.51.100.1", TransitAsnA, DiscoveryMethod.DirectRouter),
                Transit("198.51.100.2", TransitAsnA, DiscoveryMethod.DirectRouter),
                Transit("198.51.100.3", TransitAsnA, DiscoveryMethod.DirectRouter),
            },
        };

        UpstreamRediscoveryService.BuildCandidateSignature(state)
            .Should().BeEquivalentTo(new[] { $"access:as{AccessAsn}", $"transit:as{TransitAsnA}" });
    }

    [Fact]
    public void Candidate_includes_unreachable_hops_reachability_independent()
    {
        var state = new UpstreamTracerState
        {
            TransitAsns =
            {
                Transit("198.51.100.1", TransitAsnA, DiscoveryMethod.DirectRouter, enabled: true),
                Transit("198.51.100.2", TransitAsnB, DiscoveryMethod.DirectRouter, enabled: false, unreachable: true),
            },
        };

        UpstreamRediscoveryService.BuildCandidateSignature(state)
            .Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnA}", $"transit:as{TransitAsnB}" });
    }

    [Fact]
    public void Candidate_maps_pathproxy_to_path_namespace()
    {
        var state = new UpstreamTracerState
        {
            TransitAsns = { Transit(null, PathAsn, DiscoveryMethod.PathProxy, pathProxy: "203.0.113.5") },
        };

        UpstreamRediscoveryService.BuildCandidateSignature(state)
            .Should().BeEquivalentTo(new[] { $"path:as{PathAsn}" });
    }

    // ---- Committed views ----

    [Fact]
    public async Task Monitored_view_includes_userprovided_wan_agnostic_and_auto_this_wan()
    {
        _db.MonitoringTargets.AddRange(
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter),
            Target("198.51.100.9", MonitoringTargetType.Transit, TransitAsnB, "wan", DiscoveryMethod.DirectRouter, enabled: false),
            // UserProvided with empty WanInterface - still in monitored (WAN-agnostic)
            Target("203.0.113.1", MonitoringTargetType.Transit, UserAsn, "", DiscoveryMethod.UserProvided),
            // other-WAN auto - excluded entirely
            Target("198.51.100.10", MonitoringTargetType.Transit, PathAsn, "wan2", DiscoveryMethod.DirectRouter),
            // user custom (no method) - excluded entirely
            Target("203.0.113.50", MonitoringTargetType.Custom, null, "wan", method: null));
        await _db.SaveChangesAsync();

        var (monitored, _) = await UpstreamRediscoveryService.BuildCommittedViewsAsync(_db, "wan", CancellationToken.None);

        monitored.Should().BeEquivalentTo(new[]
        {
            $"transit:as{TransitAsnA}", $"transit:as{TransitAsnB}", $"transit:as{UserAsn}"
        });
    }

    [Fact]
    public async Task RemovalEligible_requires_auto_evidence_and_an_enabled_row()
    {
        _db.MonitoringTargets.AddRange(
            // enabled auto - eligible
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter),
            // disabled auto with no enabled sibling - fully-disabled ASN, nothing to pause, not eligible
            Target("198.51.100.9", MonitoringTargetType.Transit, TransitAsnB, "wan", DiscoveryMethod.DirectRouter, enabled: false),
            // UserProvided only - no auto evidence the ASN was ever on the path, not eligible
            Target("203.0.113.1", MonitoringTargetType.Transit, UserAsn, "wan", DiscoveryMethod.UserProvided),
            // other WAN - excluded
            Target("198.51.100.10", MonitoringTargetType.Transit, PathAsn, "wan2", DiscoveryMethod.DirectRouter));
        await _db.SaveChangesAsync();

        var (_, removalEligible) = await UpstreamRediscoveryService.BuildCommittedViewsAsync(_db, "wan", CancellationToken.None);

        removalEligible.Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnA}" });
    }

    [Fact]
    public async Task RemovalEligible_keeps_paused_auto_asn_with_enabled_manual_sibling()
    {
        // The dangling-manual case: the flaky auto target was paused, but a hand-added target in
        // the same ASN is still enabled - the ASN must stay eligible so the sibling gets caught
        // when the ASN goes dark. The manual row is WAN-agnostic (empty WanInterface).
        _db.MonitoringTargets.AddRange(
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter, enabled: false),
            Target("203.0.113.1", MonitoringTargetType.Transit, TransitAsnA, "", DiscoveryMethod.UserProvided));
        await _db.SaveChangesAsync();

        var (_, removalEligible) = await UpstreamRediscoveryService.BuildCommittedViewsAsync(_db, "wan", CancellationToken.None);

        removalEligible.Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnA}" });
    }

    // ---- EvaluateChange ----

    [Fact]
    public void Added_is_discovered_not_monitored()
    {
        var monitored = Set($"transit:as{TransitAsnA}");
        var candidate = Set($"transit:as{TransitAsnA}", $"transit:as{TransitAsnB}");

        var eval = UpstreamRediscoveryService.EvaluateChange(monitored, Set(), candidate, Empty, Now, Gate, 3);

        eval.Added.Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnB}" });
        eval.RemovalCandidates.Should().BeEmpty();
    }

    [Fact]
    public void UserProvided_in_monitored_suppresses_added()
    {
        // Discovery finds the hand-added Cogent ASN; it's already monitored, so not "added".
        var monitored = Set($"transit:as{UserAsn}");
        var candidate = Set($"transit:as{UserAsn}");

        var eval = UpstreamRediscoveryService.EvaluateChange(monitored, Set(), candidate, Empty, Now, Gate, 3);

        eval.Added.Should().BeEmpty();
    }

    [Fact]
    public void Missing_asn_increments_counter_but_does_not_flag_below_threshold()
    {
        var autoEnabled = Set($"transit:as{TransitAsnA}");
        var candidate = Set(); // A missing this run

        var eval = UpstreamRediscoveryService.EvaluateChange(Set(), autoEnabled, candidate, Empty, Now, Gate, 3);

        eval.NewMisses.Should().ContainKey($"transit:as{TransitAsnA}").WhoseValue.Count.Should().Be(1);
        eval.NewMisses[$"transit:as{TransitAsnA}"].LastIncrementUtc.Should().Be(Now);
        eval.RemovalCandidates.Should().BeEmpty();
    }

    [Fact]
    public void Missing_asn_becomes_removal_candidate_at_threshold()
    {
        var autoEnabled = Set($"transit:as{TransitAsnA}");
        var candidate = Set();
        var prior = Prior($"transit:as{TransitAsnA}", 2); // last increment well past the gate

        var eval = UpstreamRediscoveryService.EvaluateChange(Set(), autoEnabled, candidate, prior, Now, Gate, 3);

        eval.NewMisses[$"transit:as{TransitAsnA}"].Count.Should().Be(3);
        eval.RemovalCandidates.Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnA}" });
    }

    [Fact]
    public void Reappearing_asn_resets_its_counter()
    {
        var autoEnabled = Set($"transit:as{TransitAsnA}");
        var candidate = Set($"transit:as{TransitAsnA}"); // present again
        var prior = Prior($"transit:as{TransitAsnA}", 2);

        var eval = UpstreamRediscoveryService.EvaluateChange(Set(), autoEnabled, candidate, prior, Now, Gate, 3);

        eval.NewMisses.Should().NotContainKey($"transit:as{TransitAsnA}"); // pruned by omission
        eval.RemovalCandidates.Should().BeEmpty();
    }

    [Fact]
    public void Miss_within_gate_holds_count_and_timestamp_instead_of_incrementing()
    {
        // Absent again, but only 1h since the last increment (< 6h gate): count must NOT advance,
        // and the timestamp must stay put so the next in-window miss is also held.
        var autoEnabled = Set($"transit:as{TransitAsnA}");
        var candidate = Set();
        var lastInc = Now.AddHours(-1);
        var prior = Prior($"transit:as{TransitAsnA}", 2, hoursAgo: 1);

        var eval = UpstreamRediscoveryService.EvaluateChange(Set(), autoEnabled, candidate, prior, Now, Gate, 3);

        eval.NewMisses[$"transit:as{TransitAsnA}"].Count.Should().Be(2); // held, not 3
        eval.NewMisses[$"transit:as{TransitAsnA}"].LastIncrementUtc.Should().Be(lastInc);
        eval.RemovalCandidates.Should().BeEmpty();
    }

    [Fact]
    public void Miss_after_gate_elapses_increments()
    {
        var autoEnabled = Set($"transit:as{TransitAsnA}");
        var candidate = Set();
        var prior = Prior($"transit:as{TransitAsnA}", 1, hoursAgo: 6); // exactly at the gate

        var eval = UpstreamRediscoveryService.EvaluateChange(Set(), autoEnabled, candidate, prior, Now, Gate, 3);

        eval.NewMisses[$"transit:as{TransitAsnA}"].Count.Should().Be(2);
        eval.NewMisses[$"transit:as{TransitAsnA}"].LastIncrementUtc.Should().Be(Now);
    }

    [Fact]
    public void Confirmed_asn_gated_this_run_stays_a_removal_candidate()
    {
        // Already at threshold and absent again within the gate: it holds at 3 (doesn't climb) but
        // must remain a removal candidate so a gated recheck can't un-confirm it.
        var autoEnabled = Set($"transit:as{TransitAsnA}");
        var candidate = Set();
        var prior = Prior($"transit:as{TransitAsnA}", 3, hoursAgo: 1);

        var eval = UpstreamRediscoveryService.EvaluateChange(Set(), autoEnabled, candidate, prior, Now, Gate, 3);

        eval.NewMisses[$"transit:as{TransitAsnA}"].Count.Should().Be(3);
        eval.RemovalCandidates.Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnA}" });
    }

    [Fact]
    public void Fully_disabled_asn_neither_added_nor_removed()
    {
        // Monitored (suppresses added) but not removal-eligible (every target in the ASN is
        // disabled - nothing to pause). Discovery still finds the ASN, so nothing flags - and
        // nothing would get silently re-enabled by a commit.
        var monitored = Set($"transit:as{TransitAsnB}");
        var removalEligible = Set(); // fully disabled => not eligible for removed
        var candidate = Set($"transit:as{TransitAsnB}");

        var eval = UpstreamRediscoveryService.EvaluateChange(monitored, removalEligible, candidate, Empty, Now, Gate, 3);

        eval.Added.Should().BeEmpty();
        eval.RemovalCandidates.Should().BeEmpty();
        eval.NewMisses.Should().BeEmpty();
    }

    // ---- BuildRemovedTransitAsns (pause entries) ----

    [Fact]
    public async Task RemovedTransitAsns_counts_enabled_auto_and_wan_agnostic_manual_targets()
    {
        _db.MonitoringTargets.AddRange(
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter),
            // hand-added, WAN-agnostic - counted and reported as manual
            Target("203.0.113.1", MonitoringTargetType.Transit, TransitAsnA, "", DiscoveryMethod.UserProvided),
            // disabled - already paused, not counted
            Target("198.51.100.2", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter, enabled: false),
            // other-WAN auto - not counted
            Target("198.51.100.3", MonitoringTargetType.Transit, TransitAsnA, "wan2", DiscoveryMethod.DirectRouter),
            // other ASN, not in the confirmed list - no entry
            Target("198.51.100.4", MonitoringTargetType.Transit, TransitAsnB, "wan", DiscoveryMethod.DirectRouter));
        await _db.SaveChangesAsync();

        var result = await UpstreamRediscoveryService.BuildRemovedTransitAsnsAsync(
            _db, "wan", new[] { $"transit:as{TransitAsnA}" }, CancellationToken.None);

        var entry = result.Should().ContainSingle().Subject;
        entry.AsnNumber.Should().Be(TransitAsnA);
        entry.TargetCount.Should().Be(2);
        entry.ManualCount.Should().Be(1);
        entry.Keep.Should().BeFalse();
    }

    [Fact]
    public async Task RemovedTransitAsns_ignores_non_transit_keys_address_keys_and_fully_disabled_asns()
    {
        _db.MonitoringTargets.AddRange(
            // fully disabled ASN - confirmed removed but nothing to pause -> no entry, no nag
            Target("198.51.100.9", MonitoringTargetType.Transit, TransitAsnB, "wan", DiscoveryMethod.DirectRouter, enabled: false),
            // enabled access target - access keys aren't handled by this detector
            Target("192.0.2.1", MonitoringTargetType.AccessIsp, AccessAsn, "wan", DiscoveryMethod.DirectRouter));
        await _db.SaveChangesAsync();

        var result = await UpstreamRediscoveryService.BuildRemovedTransitAsnsAsync(
            _db, "wan",
            new[] { $"transit:as{TransitAsnB}", $"access:as{AccessAsn}", "transit:198.51.100.77", $"path:as{PathAsn}" },
            CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ---- Miss-counter clearing on commit ----

    [Fact]
    public async Task ClearMissCountKeys_removes_only_the_given_keys()
    {
        _db.SystemSettings.Add(new SystemSetting
        {
            Key = SystemSettingKeys.UpstreamAbsentAsnCountsPrefix + "wan",
            Value = $"{{\"transit:as{TransitAsnA}\":3,\"access:as{AccessAsn}\":2}}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await UpstreamRediscoveryService.ClearMissCountKeysAsync(
            _db, "wan", new[] { $"transit:as{TransitAsnA}" }, CancellationToken.None);
        await _db.SaveChangesAsync();

        var row = await _db.SystemSettings.SingleAsync(
            s => s.Key == SystemSettingKeys.UpstreamAbsentAsnCountsPrefix + "wan");
        row.Value.Should().Contain($"access:as{AccessAsn}").And.NotContain($"transit:as{TransitAsnA}");
    }

    // ---- EvaluateCompletedRunAsync (pending vs confirmed staging) ----

    [Fact]
    public async Task CompletedRun_stages_pending_tracking_when_below_threshold()
    {
        // Dangling-manual AS: auto evidence disabled, manual enabled, absent this run, prior count 1
        // last incremented well past the gate so this run advances it.
        SeedDanglingManualAsn(TransitAsnA, priorCount: 1, lastIncrementUtc: DateTime.UtcNow.AddHours(-24));
        var state = AbsentRunState(TransitAsnA);

        await UpstreamRediscoveryService.EvaluateCompletedRunAsync(_db, state, CancellationToken.None);

        state.RemovedTransitAsns.Should().BeEmpty();
        var p = state.PendingRemovalTransitAsns.Should().ContainSingle().Subject;
        p.AsnNumber.Should().Be(TransitAsnA);
        p.MissCount.Should().Be(2);       // 1 -> 2
        p.RunsRemaining.Should().Be(1);   // threshold 3 - 2
        p.TargetCount.Should().Be(1);     // only the enabled manual row would pause
        p.ManualCount.Should().Be(1);
    }

    [Fact]
    public async Task CompletedRun_confirms_and_leaves_pending_empty_at_threshold()
    {
        // Same dangling-manual AS, but prior count 2 (past the gate) -> this run hits threshold 3.
        SeedDanglingManualAsn(TransitAsnA, priorCount: 2, lastIncrementUtc: DateTime.UtcNow.AddHours(-24));
        var state = AbsentRunState(TransitAsnA);

        await UpstreamRediscoveryService.EvaluateCompletedRunAsync(_db, state, CancellationToken.None);

        state.PendingRemovalTransitAsns.Should().BeEmpty();
        var r = state.RemovedTransitAsns.Should().ContainSingle().Subject;
        r.AsnNumber.Should().Be(TransitAsnA);
        r.TargetCount.Should().Be(1);
        r.ManualCount.Should().Be(1);
    }

    [Fact]
    public async Task CompletedRun_holds_count_when_within_gate()
    {
        // Prior increment 1h ago (< 6h gate): an absent run must NOT advance the count - it stays
        // pending at 2 rather than confirming, so rapid re-traces can't rush a removal.
        SeedDanglingManualAsn(TransitAsnA, priorCount: 2, lastIncrementUtc: DateTime.UtcNow.AddHours(-1));
        var state = AbsentRunState(TransitAsnA);

        await UpstreamRediscoveryService.EvaluateCompletedRunAsync(_db, state, CancellationToken.None);

        state.RemovedTransitAsns.Should().BeEmpty();
        var p = state.PendingRemovalTransitAsns.Should().ContainSingle().Subject;
        p.MissCount.Should().Be(2);
        p.RunsRemaining.Should().Be(1);
    }

    [Fact]
    public async Task CompletedRun_confirms_once_gate_has_elapsed()
    {
        // Same count, but the last increment was 7h ago (> 6h gate): this run advances 2 -> 3.
        SeedDanglingManualAsn(TransitAsnA, priorCount: 2, lastIncrementUtc: DateTime.UtcNow.AddHours(-7));
        var state = AbsentRunState(TransitAsnA);

        await UpstreamRediscoveryService.EvaluateCompletedRunAsync(_db, state, CancellationToken.None);

        state.PendingRemovalTransitAsns.Should().BeEmpty();
        state.RemovedTransitAsns.Should().ContainSingle().Which.AsnNumber.Should().Be(TransitAsnA);
    }

    [Fact]
    public async Task CompletedRun_reads_legacy_intonly_counter_and_advances()
    {
        // Legacy stored format (a bare int, no timestamp) must load as immediately gate-eligible so
        // an in-flight counter from before the gate keeps advancing after upgrade.
        SeedLegacyDanglingManualAsn(TransitAsnA, priorCount: 2);
        var state = AbsentRunState(TransitAsnA);

        await UpstreamRediscoveryService.EvaluateCompletedRunAsync(_db, state, CancellationToken.None);

        state.PendingRemovalTransitAsns.Should().BeEmpty();
        state.RemovedTransitAsns.Should().ContainSingle().Which.AsnNumber.Should().Be(TransitAsnA);
    }

    // ---- helpers ----

    private void SeedDanglingManualTargets(int asn)
    {
        _db.MonitoringTargets.AddRange(
            Target("198.51.100.1", MonitoringTargetType.Transit, asn, "wan", DiscoveryMethod.DirectRouter, enabled: false),
            Target("203.0.113.1", MonitoringTargetType.Transit, asn, "", DiscoveryMethod.UserProvided));
    }

    // Dangling-manual ASN with a current-format counter (count + last-increment timestamp).
    private void SeedDanglingManualAsn(int asn, int priorCount, DateTime lastIncrementUtc)
    {
        SeedDanglingManualTargets(asn);
        _db.SystemSettings.Add(new SystemSetting
        {
            Key = SystemSettingKeys.UpstreamAbsentAsnCountsPrefix + "wan",
            Value = $"{{\"transit:as{asn}\":{{\"c\":{priorCount},\"t\":\"{lastIncrementUtc:o}\"}}}}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    // Dangling-manual ASN with a legacy counter (a bare int, no timestamp).
    private void SeedLegacyDanglingManualAsn(int asn, int priorCount)
    {
        SeedDanglingManualTargets(asn);
        _db.SystemSettings.Add(new SystemSetting
        {
            Key = SystemSettingKeys.UpstreamAbsentAsnCountsPrefix + "wan",
            Value = $"{{\"transit:as{asn}\":{priorCount}}}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    // Run state whose candidate signature excludes `absentAsn` (an unrelated ASN is present so the
    // signature isn't empty), i.e. the given ASN went off-path this run.
    private static UpstreamTracerState AbsentRunState(int absentAsn)
    {
        var other = absentAsn == TransitAsnB ? TransitAsnA : TransitAsnB;
        return new UpstreamTracerState
        {
            WanInterface = "wan",
            TransitAsns = { Transit("198.51.100.9", other, DiscoveryMethod.DirectRouter) },
        };
    }


    private static HashSet<string> Set(params string[] keys) => new(keys, StringComparer.OrdinalIgnoreCase);

    // Fixed evaluation clock + gate for the pure EvaluateChange tests.
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Gate = TimeSpan.FromHours(6);

    private static readonly Dictionary<string, UpstreamRediscoveryService.MissRecord> Empty =
        new(StringComparer.OrdinalIgnoreCase);

    // A prior-miss map with one entry whose last increment was `hoursAgo` before Now (default well
    // past the gate, so the next miss increments).
    private static Dictionary<string, UpstreamRediscoveryService.MissRecord> Prior(
        string key, int count, double hoursAgo = 24) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [key] = new UpstreamRediscoveryService.MissRecord(count, Now.AddHours(-hoursAgo)),
        };

    private static AccessHopCandidate Hop(string address, int? asn, bool enabled = true, bool unreachable = false) => new()
    {
        TargetId = $"access-{address}",
        Label = $"Access {address}",
        Address = address,
        AsnNumber = asn,
        Method = DiscoveryMethod.DirectRouter,
        Enabled = enabled,
        Unreachable = unreachable,
    };

    private static TransitAsnCandidate Transit(string? hopAddress, int asn, DiscoveryMethod method,
        bool enabled = true, string? pathProxy = null, bool unreachable = false) => new()
    {
        AsnName = $"AS{asn}",
        HopAddress = hopAddress,
        AsnNumber = asn,
        Method = method,
        Enabled = enabled,
        PathProxyTarget = pathProxy,
        Unreachable = unreachable,
    };

    private static MonitoringTarget Target(string address, MonitoringTargetType type, int? asn,
        string wan, DiscoveryMethod? method, bool enabled = true) => new()
    {
        TargetId = $"{type}-{address}",
        Name = $"{type} {address}",
        Address = address,
        TargetType = type,
        AsnNumber = asn,
        WanInterface = wan,
        DiscoveryMethod = method,
        Enabled = enabled,
    };
}
