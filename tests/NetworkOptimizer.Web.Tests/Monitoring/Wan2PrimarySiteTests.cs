using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Primary is a ROLE, not a name. WAN1/WAN2/WAN3 are arbitrary labels in UniFi Network and any of
/// them can hold the primary role - a site whose WAN2 is primary and WAN1 is the failover is an
/// ordinary configuration, not an exotic one. Every "which WAN is primary" answer therefore has to
/// come from the configured primary network group, never from the conventional "wan"-first
/// ordering. This fixture is that site: WAN2 primary, WAN1 failover, with a context on each.
/// </summary>
public class Wan2PrimarySiteTests
{
    private static NetworkInfo Wan(string group) => new()
    {
        Id = group,
        Name = group,
        Purpose = "wan",
        Enabled = true,
        WanNetworkgroup = group,
    };

    private static MonitoringTarget Target(string id, string? wan) => new()
    {
        TargetId = id,
        Name = id,
        Address = "192.0.2.1",
        WanInterface = wan,
    };

    // ─── The scope key the primary report resolves ───

    [Theory]
    [InlineData("WAN", "wan")]
    [InlineData("WAN2", "wan2")]
    [InlineData("WAN3", "wan3")]
    [InlineData("wan2", "wan2")]
    public void ConfiguredPrimaryWanKey_TakesWhicheverGroupHoldsTheRole(string group, string expected)
    {
        IspHealthService.ConfiguredPrimaryWanKey(Wan(group)).Should().Be(expected);
    }

    [Fact]
    public void ConfiguredPrimaryWanKey_IsNullWhenTheConsoleCannotSay()
    {
        // Null is the signal to fall through to the documented offline guess, not "it is wan".
        IspHealthService.ConfiguredPrimaryWanKey(null).Should().BeNull();
        IspHealthService.ConfiguredPrimaryWanKey(new NetworkInfo { Purpose = "wan" }).Should().BeNull();
    }

    // ─── Target scoping on that site ───

    [Fact]
    public void PrimaryScope_OnAWan2PrimarySite_KeepsWan2RowsAndTheUnstampedOnes()
    {
        // Unstamped rows are primary-path measurements wherever the role sits; wan1's rows are
        // the FAILOVER's here and must never grade the primary.
        var targets = new List<MonitoringTarget>
        {
            Target("legacy", null),
            Target("hop-wan2", "wan2"),
            Target("hop-wan2-upper", "WAN2"),
            Target("hop-wan", "wan"),
            Target("hop-wan1", "wan1"),
        };

        IspHealthService.ScopeTargetsToWan(targets, "wan2", includeUnassigned: true)
            .Select(t => t.TargetId).Should().Equal("legacy", "hop-wan2", "hop-wan2-upper");
    }

    [Fact]
    public void FailoverScope_OnAWan2PrimarySite_OwnsTheWanRowsAndNoUnstampedOnes()
    {
        var targets = new List<MonitoringTarget>
        {
            Target("legacy", null),
            Target("hop-wan", "wan"),
            Target("hop-wan1", "wan1"),   // the legacy alias is the same WAN as "wan"
            Target("hop-wan2", "wan2"),
        };

        IspHealthService.ScopeTargetsToWan(targets, "wan", includeUnassigned: false)
            .Select(t => t.TargetId).Should().Equal("hop-wan", "hop-wan1");
    }

    [Fact]
    public void PrimaryScope_OnAWan2PrimarySite_ReadsWan2sTagsNeverWan1s()
    {
        var contexts = new[]
        {
            new WanContext { Name = "fiber", WanInterface = "wan2" },
            new WanContext { Name = "cable-failover", WanInterface = "wan" },
        };

        var scope = IspHealthService.BuildWanScope(contexts, "wan2", primaryScope: true);

        scope.IncludeUntagged.Should().BeTrue();
        scope.WanTags.Should().BeEquivalentTo("wan2", "fiber");
    }

    // ─── Upstream discovery rehydrate ───

    [Fact]
    public void PickRehydrateContext_TakesTheConfiguredPrimarysRowNotTheWanOne()
    {
        // The bug this pins: a WAN2-primary site rehydrating the primary panel from WAN1's row
        // presents the failover's hops as the primary's upstream path.
        var contexts = new List<WanDiscoveryContext>
        {
            new() { WanInterface = "wan", LastDiscoveryAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { WanInterface = "wan2", LastDiscoveryAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc) },
        };

        UpstreamTracerService.PickRehydrateContext(contexts, boundWanInterface: null, configuredPrimaryKey: "wan2")
            !.WanInterface.Should().Be("wan2");
    }

    [Fact]
    public void PickRehydrateContext_StillLetsABoundTracerReadItsOwnWan()
    {
        var contexts = new List<WanDiscoveryContext>
        {
            new() { WanInterface = "wan" },
            new() { WanInterface = "wan2" },
        };

        UpstreamTracerService.PickRehydrateContext(contexts, boundWanInterface: "wan", configuredPrimaryKey: "wan2")
            !.WanInterface.Should().Be("wan");
    }

    [Fact]
    public void PickRehydrateContext_FallsBackToTheDocumentedGuessOnlyWhenTheConsoleIsSilent()
    {
        // Offline last resort: the conventional "wan" row, then recency. Wrong on exactly this
        // site - which is why the configured key is asked for first and this is a documented guess.
        var contexts = new List<WanDiscoveryContext>
        {
            new() { WanInterface = "wan2", LastDiscoveryAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { WanInterface = "wan", LastDiscoveryAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc) },
        };

        UpstreamTracerService.PickRehydrateContext(contexts, boundWanInterface: null, configuredPrimaryKey: null)
            !.WanInterface.Should().Be("wan");
    }

    [Fact]
    public void PickRehydrateContext_TakesTheOnlyRowWhenTheConfiguredPrimaryHasNoneYet()
    {
        var contexts = new List<WanDiscoveryContext> { new() { WanInterface = "wan" } };

        UpstreamTracerService.PickRehydrateContext(contexts, boundWanInterface: null, configuredPrimaryKey: "wan2")
            !.WanInterface.Should().Be("wan");
    }

    // ─── The offline guess, stated as a guess ───

    [Fact]
    public void ResolvePrimaryWanKey_IsTheWanFirstGuessAndSaysSoOnAWan2PrimarySite()
    {
        // Pinned deliberately: with the console silent there is nothing better to ask, so the
        // offline answer on a WAN2-primary site is "wan" - wrong, self-correcting on the next
        // connected compute, and never reached while ConfiguredPrimaryWanKey can answer.
        var contexts = new[]
        {
            new WanDiscoveryContext { WanInterface = "wan2" },
            new WanDiscoveryContext { WanInterface = "wan" },
        };

        IspHealthService.ResolvePrimaryWanKey(contexts).Should().Be("wan");
    }
}
