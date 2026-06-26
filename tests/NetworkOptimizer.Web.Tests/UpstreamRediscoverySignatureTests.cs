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
    public void Candidate_excludes_disabled_hops()
    {
        var state = new UpstreamTracerState
        {
            TransitAsns =
            {
                Transit("198.51.100.1", TransitAsnA, DiscoveryMethod.DirectRouter, enabled: true),
                Transit("198.51.100.2", TransitAsnB, DiscoveryMethod.DirectRouter, enabled: false),
            },
        };

        UpstreamRediscoveryService.BuildCandidateSignature(state)
            .Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnA}" });
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
    public async Task Committed_collapses_ecmp_and_excludes_disabled_other_wan_and_custom()
    {
        _db.MonitoringTargets.AddRange(
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter),
            Target("198.51.100.2", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter),
            Target("192.0.2.1", MonitoringTargetType.AccessIsp, AccessAsn, "wan", DiscoveryMethod.DirectRouter),
            Target("198.51.100.9", MonitoringTargetType.Transit, TransitAsnB, "wan", DiscoveryMethod.DirectRouter, enabled: false),
            Target("198.51.100.10", MonitoringTargetType.Transit, TransitAsnB, "wan2", DiscoveryMethod.DirectRouter),
            Target("203.0.113.50", MonitoringTargetType.Custom, null, "wan", method: null));
        await _db.SaveChangesAsync();

        var sig = await UpstreamRediscoveryService.BuildCommittedSignatureAsync(_db, "wan", CancellationToken.None);

        sig.Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnA}", $"access:as{AccessAsn}" });
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

    // ---- helpers ----

    private static AccessHopCandidate Hop(string address, int? asn, bool enabled = true) => new()
    {
        TargetId = $"access-{address}",
        Label = $"Access {address}",
        Address = address,
        AsnNumber = asn,
        Method = DiscoveryMethod.DirectRouter,
        Enabled = enabled,
    };

    private static TransitAsnCandidate Transit(string? hopAddress, int asn, DiscoveryMethod method,
        bool enabled = true, string? pathProxy = null) => new()
    {
        AsnName = $"AS{asn}",
        HopAddress = hopAddress,
        AsnNumber = asn,
        Method = method,
        Enabled = enabled,
        PathProxyTarget = pathProxy,
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
