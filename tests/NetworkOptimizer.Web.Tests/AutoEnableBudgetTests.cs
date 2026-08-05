using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class AutoEnableBudgetTests
{
    private static UpstreamTracerState BuildState(int accessHops, int transitRouters, int pathEndpoints)
    {
        var state = new UpstreamTracerState();
        for (var i = 1; i <= accessHops; i++)
            state.AccessHops.Add(new AccessHopCandidate
            {
                TargetId = $"access-{i}",
                Label = $"Access {i}",
                Address = $"192.0.2.{i}",
                HopNumber = i,
                Enabled = true,
            });
        for (var i = 1; i <= transitRouters; i++)
            state.TransitAsns.Add(new TransitAsnCandidate
            {
                AsnNumber = 64500 + i,
                AsnName = $"Transit{i}",
                Method = DiscoveryMethod.DirectRouter,
                HopAddress = $"198.51.100.{i}",
                Enabled = true,
            });
        for (var i = 1; i <= pathEndpoints; i++)
            state.TransitAsns.Add(new TransitAsnCandidate
            {
                AsnNumber = 64600 + i,
                AsnName = $"Endpoint{i}",
                Method = DiscoveryMethod.PathProxy,
                PathProxyTarget = $"203.0.113.{i}",
                Enabled = true,
            });
        return state;
    }

    private static int EnabledOf(UpstreamTracerState state, DiscoveryMethod method) =>
        state.TransitAsns.Count(t => t.Method == method && t.Enabled);

    [Fact]
    public void No_budget_leaves_every_candidate_ticked()
    {
        var state = BuildState(accessHops: 6, transitRouters: 6, pathEndpoints: 6);

        UpstreamTracerService.ApplyAutoEnableBudget(state, null);

        state.AccessHops.Should().OnlyContain(h => h.Enabled);
        state.TransitAsns.Should().OnlyContain(t => t.Enabled);
    }

    [Fact]
    public void Every_bucket_keeps_a_share_of_a_tight_budget()
    {
        // The failure this guards: access hops taken first and in full left one endpoint ticked,
        // so the site could see its first mile and not whether anything it reaches was up.
        var state = BuildState(accessHops: 12, transitRouters: 6, pathEndpoints: 9);

        UpstreamTracerService.ApplyAutoEnableBudget(state, 8);

        state.AccessHops.Count(h => h.Enabled).Should().Be(3);
        EnabledOf(state, DiscoveryMethod.DirectRouter).Should().Be(3);
        EnabledOf(state, DiscoveryMethod.PathProxy).Should().Be(2);
    }

    [Fact]
    public void A_bucket_that_runs_out_hands_its_share_to_the_others()
    {
        var state = BuildState(accessHops: 1, transitRouters: 6, pathEndpoints: 6);

        UpstreamTracerService.ApplyAutoEnableBudget(state, 7);

        state.AccessHops.Count(h => h.Enabled).Should().Be(1);
        EnabledOf(state, DiscoveryMethod.DirectRouter).Should().Be(3);
        EnabledOf(state, DiscoveryMethod.PathProxy).Should().Be(3);
    }

    [Fact]
    public void Unreachable_candidates_are_neither_ticked_nor_charged_to_the_budget()
    {
        // The reachability gate runs BEFORE this and turns them off. Switching one back on because
        // it fell inside the budget hands over a target known not to answer, and spends one of the
        // few slots a metered WAN gets doing it.
        var state = BuildState(accessHops: 4, transitRouters: 0, pathEndpoints: 0);
        foreach (var hop in state.AccessHops.Take(2))
        {
            hop.Unreachable = true;
            hop.Enabled = false;
        }

        UpstreamTracerService.ApplyAutoEnableBudget(state, 2);

        state.AccessHops.Where(h => h.Unreachable).Should().OnlyContain(h => !h.Enabled);
        state.AccessHops.Where(h => !h.Unreachable).Should().OnlyContain(h => h.Enabled);
    }

    [Fact]
    public void Candidates_beyond_the_budget_are_turned_off()
    {
        var state = BuildState(accessHops: 4, transitRouters: 0, pathEndpoints: 0);

        UpstreamTracerService.ApplyAutoEnableBudget(state, 2);

        state.AccessHops.Count(h => h.Enabled).Should().Be(2);
        state.AccessHops.Where(h => h.Enabled).Should().OnlyContain(h => h.HopNumber <= 2);
    }
}
