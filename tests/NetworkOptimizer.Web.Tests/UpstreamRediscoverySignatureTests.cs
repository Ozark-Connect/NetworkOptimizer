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
/// "added" but are never eligible for "removed".
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
    public async Task AutoEnabled_view_is_enabled_auto_this_wan_only()
    {
        _db.MonitoringTargets.AddRange(
            Target("198.51.100.1", MonitoringTargetType.Transit, TransitAsnA, "wan", DiscoveryMethod.DirectRouter),
            // disabled - excluded from removed-eligibility
            Target("198.51.100.9", MonitoringTargetType.Transit, TransitAsnB, "wan", DiscoveryMethod.DirectRouter, enabled: false),
            // UserProvided - excluded from removed-eligibility
            Target("203.0.113.1", MonitoringTargetType.Transit, UserAsn, "wan", DiscoveryMethod.UserProvided),
            // other WAN - excluded
            Target("198.51.100.10", MonitoringTargetType.Transit, PathAsn, "wan2", DiscoveryMethod.DirectRouter));
        await _db.SaveChangesAsync();

        var (_, autoEnabled) = await UpstreamRediscoveryService.BuildCommittedViewsAsync(_db, "wan", CancellationToken.None);

        autoEnabled.Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnA}" });
    }

    // ---- EvaluateChange ----

    [Fact]
    public void Added_is_discovered_not_monitored()
    {
        var monitored = Set($"transit:as{TransitAsnA}");
        var candidate = Set($"transit:as{TransitAsnA}", $"transit:as{TransitAsnB}");

        var eval = UpstreamRediscoveryService.EvaluateChange(monitored, Set(), candidate, Empty, 3);

        eval.Added.Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnB}" });
        eval.RemovalCandidates.Should().BeEmpty();
    }

    [Fact]
    public void UserProvided_in_monitored_suppresses_added()
    {
        // Discovery finds the hand-added Cogent ASN; it's already monitored, so not "added".
        var monitored = Set($"transit:as{UserAsn}");
        var candidate = Set($"transit:as{UserAsn}");

        var eval = UpstreamRediscoveryService.EvaluateChange(monitored, Set(), candidate, Empty, 3);

        eval.Added.Should().BeEmpty();
    }

    [Fact]
    public void Missing_asn_increments_counter_but_does_not_flag_below_threshold()
    {
        var autoEnabled = Set($"transit:as{TransitAsnA}");
        var candidate = Set(); // A missing this run

        var eval = UpstreamRediscoveryService.EvaluateChange(Set(), autoEnabled, candidate, Empty, 3);

        eval.NewMissCounts.Should().ContainKey($"transit:as{TransitAsnA}").WhoseValue.Should().Be(1);
        eval.RemovalCandidates.Should().BeEmpty();
    }

    [Fact]
    public void Missing_asn_becomes_removal_candidate_at_threshold()
    {
        var autoEnabled = Set($"transit:as{TransitAsnA}");
        var candidate = Set();
        var prior = new Dictionary<string, int> { [$"transit:as{TransitAsnA}"] = 2 };

        var eval = UpstreamRediscoveryService.EvaluateChange(Set(), autoEnabled, candidate, prior, 3);

        eval.NewMissCounts[$"transit:as{TransitAsnA}"].Should().Be(3);
        eval.RemovalCandidates.Should().BeEquivalentTo(new[] { $"transit:as{TransitAsnA}" });
    }

    [Fact]
    public void Reappearing_asn_resets_its_counter()
    {
        var autoEnabled = Set($"transit:as{TransitAsnA}");
        var candidate = Set($"transit:as{TransitAsnA}"); // present again
        var prior = new Dictionary<string, int> { [$"transit:as{TransitAsnA}"] = 2 };

        var eval = UpstreamRediscoveryService.EvaluateChange(Set(), autoEnabled, candidate, prior, 3);

        eval.NewMissCounts.Should().NotContainKey($"transit:as{TransitAsnA}"); // pruned by omission
        eval.RemovalCandidates.Should().BeEmpty();
    }

    [Fact]
    public void Disabled_flaky_target_neither_added_nor_removed()
    {
        // Monitored (suppresses added) but NOT auto-enabled (not removal-eligible). Discovery still
        // finds the ASN, so nothing flags - and nothing would get silently re-enabled by a commit.
        var monitored = Set($"transit:as{TransitAsnB}");
        var autoEnabled = Set(); // disabled => not eligible for removed
        var candidate = Set($"transit:as{TransitAsnB}");

        var eval = UpstreamRediscoveryService.EvaluateChange(monitored, autoEnabled, candidate, Empty, 3);

        eval.Added.Should().BeEmpty();
        eval.RemovalCandidates.Should().BeEmpty();
        eval.NewMissCounts.Should().BeEmpty();
    }

    // ---- helpers ----

    private static HashSet<string> Set(params string[] keys) => new(keys, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> Empty = new(StringComparer.OrdinalIgnoreCase);

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
