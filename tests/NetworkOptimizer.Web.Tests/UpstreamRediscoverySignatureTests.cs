using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Change-detection signature for the scheduled upstream re-discovery. The fix keys on a
/// stable upstream-ASN identity instead of per-hop TargetIds so traceroute ECMP churn stops
/// re-flagging the review banner, while genuine ASN-path changes still flag.
///
/// All ASNs are RFC 5398 documentation ASNs (64496-64511) and all IPs are RFC 5737
/// documentation ranges - never real network data.
/// </summary>
public class UpstreamRediscoverySignatureTests : IDisposable
{
    private const int AccessAsn = 64496;
    private const int TransitAsnA = 64497;
    private const int TransitAsnB = 64498;
    private const int PathAsn = 64499;

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

    // ---- Candidate signature (in-memory tracer state) ----

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
        // A hop that flapped the ping gate this run is still on the path - it must stay in the
        // signature so reachability noise doesn't read as a topology change.
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

    // ---- Committed signature (DB) ----

    [Fact]
    public async Task Committed_collapses_ecmp_keeps_disabled_excludes_other_wan_and_custom()
    {
        _db.MonitoringTargets.AddRange(
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter),
            Target("198.51.100.2", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter),
            Target("192.0.2.1", MonitoringTargetType.AccessIsp, AccessAsn, "wan", DiscoveryMethod.DirectRouter),
            // user-disabled - still counted (reachability-independent), so it doesn't read as a change
            Target("198.51.100.9", MonitoringTargetType.Transit, TransitAsnB, "wan", DiscoveryMethod.DirectRouter, enabled: false),
            // other WAN - excluded
            Target("198.51.100.10", MonitoringTargetType.Transit, PathAsn, "wan2", DiscoveryMethod.DirectRouter),
            // user custom (no discovery method) - excluded
            Target("203.0.113.50", MonitoringTargetType.Custom, null, "wan", method: null));
        await _db.SaveChangesAsync();

        var sig = await UpstreamRediscoveryService.BuildCommittedSignatureAsync(_db, "wan", CancellationToken.None);

        sig.Should().BeEquivalentTo(new[]
        {
            $"transit:as{TransitAsnA}", $"transit:as{TransitAsnB}", $"access:as{AccessAsn}"
        });
    }

    // ---- Convergence: the regression this fix targets ----

    [Fact]
    public async Task Stable_asn_path_with_ecmp_hop_churn_reports_no_change()
    {
        // Committed: two hop IPs for one transit ASN, plus one access hop.
        _db.MonitoringTargets.AddRange(
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter),
            Target("198.51.100.2", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter),
            Target("192.0.2.1", MonitoringTargetType.AccessIsp, AccessAsn, "wan", DiscoveryMethod.DirectRouter));
        await _db.SaveChangesAsync();

        // New run: same ASNs, entirely different hop IPs (ECMP load-balancing).
        var state = new UpstreamTracerState
        {
            WanInterface = "wan",
            AccessHops = { Hop("192.0.2.250", AccessAsn) },
            TransitAsns = { Transit("203.0.113.77", TransitAsnA, DiscoveryMethod.DirectRouter) },
        };

        var committed = await UpstreamRediscoveryService.BuildCommittedSignatureAsync(_db, "wan", CancellationToken.None);
        var candidate = UpstreamRediscoveryService.BuildCandidateSignature(state);

        committed.SetEquals(candidate).Should().BeTrue();
    }

    [Fact]
    public async Task New_transit_asn_is_reported_as_a_change()
    {
        _db.MonitoringTargets.Add(
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter));
        await _db.SaveChangesAsync();

        var state = new UpstreamTracerState
        {
            WanInterface = "wan",
            TransitAsns =
            {
                Transit("198.51.100.1", TransitAsnA, DiscoveryMethod.DirectRouter),
                Transit("203.0.113.1", TransitAsnB, DiscoveryMethod.DirectRouter),
            },
        };

        var committed = await UpstreamRediscoveryService.BuildCommittedSignatureAsync(_db, "wan", CancellationToken.None);
        var candidate = UpstreamRediscoveryService.BuildCandidateSignature(state);

        committed.SetEquals(candidate).Should().BeFalse();
        candidate.Except(committed).Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnB}" });
        committed.Except(candidate).Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_flaky_target_still_on_path_does_not_diff()
    {
        // User disabled the only target for an ASN because it was flaky. Discovery still finds
        // that ASN on the path (here via a different hop IP that's currently unreachable). The
        // disable must not read as a path change - otherwise we'd nag, and a commit would
        // silently re-enable the target the user turned off.
        _db.MonitoringTargets.AddRange(
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter),
            Target("198.51.100.2", MonitoringTargetType.Transit, TransitAsnB, "wan", DiscoveryMethod.DirectRouter, enabled: false));
        await _db.SaveChangesAsync();

        var state = new UpstreamTracerState
        {
            WanInterface = "wan",
            TransitAsns =
            {
                Transit("198.51.100.1", TransitAsnA, DiscoveryMethod.DirectRouter),
                Transit("203.0.113.9", TransitAsnB, DiscoveryMethod.DirectRouter, unreachable: true),
            },
        };

        var committed = await UpstreamRediscoveryService.BuildCommittedSignatureAsync(_db, "wan", CancellationToken.None);
        var candidate = UpstreamRediscoveryService.BuildCandidateSignature(state);

        committed.SetEquals(candidate).Should().BeTrue();
    }

    // ---- Degraded-run guard ----

    [Fact]
    public void Degraded_when_nothing_discovered()
    {
        UpstreamRediscoveryService.IsRunDegraded(new UpstreamTracerState()).Should().BeTrue();
    }

    [Fact]
    public void Degraded_when_all_access_hops_unreachable()
    {
        var state = new UpstreamTracerState
        {
            AccessHops = { Hop("192.0.2.1", AccessAsn, unreachable: true) },
            TransitAsns = { Transit("198.51.100.1", TransitAsnA, DiscoveryMethod.DirectRouter) },
        };
        UpstreamRediscoveryService.IsRunDegraded(state).Should().BeTrue();
    }

    [Fact]
    public void Degraded_when_majority_of_hops_unreachable()
    {
        var state = new UpstreamTracerState
        {
            AccessHops = { Hop("192.0.2.1", AccessAsn) },
            TransitAsns =
            {
                Transit("198.51.100.1", TransitAsnA, DiscoveryMethod.DirectRouter, unreachable: true),
                Transit("198.51.100.2", TransitAsnB, DiscoveryMethod.DirectRouter, unreachable: true),
            },
        };
        // 2 of 3 unreachable >= 50%
        UpstreamRediscoveryService.IsRunDegraded(state).Should().BeTrue();
    }

    [Fact]
    public void Not_degraded_when_mostly_reachable()
    {
        var state = new UpstreamTracerState
        {
            AccessHops = { Hop("192.0.2.1", AccessAsn) },
            TransitAsns =
            {
                Transit("198.51.100.1", TransitAsnA, DiscoveryMethod.DirectRouter),
                Transit("198.51.100.2", TransitAsnB, DiscoveryMethod.DirectRouter),
                Transit("198.51.100.3", PathAsn, DiscoveryMethod.DirectRouter, unreachable: true),
            },
        };
        // 1 of 4 unreachable < 50%
        UpstreamRediscoveryService.IsRunDegraded(state).Should().BeFalse();
    }

    // ---- helpers ----

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
