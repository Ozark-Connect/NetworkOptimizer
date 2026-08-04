using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class MeteredProbePolicyTests
{
    [Theory]
    [InlineData(AccessTechnology.Gpon)]
    [InlineData(AccessTechnology.XgsPon)]
    [InlineData(AccessTechnology.Docsis)]
    [InlineData(AccessTechnology.DirectEthernet)]
    [InlineData(AccessTechnology.PppoE)]
    [InlineData(AccessTechnology.Dsl)]
    [InlineData(AccessTechnology.Unknown)]
    [InlineData(AccessTechnology.Other)]
    public void Wireline_and_unknown_technologies_probe_as_before(AccessTechnology technology)
    {
        var plan = MeteredProbePolicy.For(technology, dataUsageEnabled: false);

        plan.Rung.Should().Be(0);
        plan.MaxAutoEnabled.Should().BeNull();
        plan.PollIntervalSeconds.Should().Be(MeteredProbePolicy.DefaultIntervalSeconds);
    }

    [Theory]
    [InlineData(AccessTechnology.Satellite)]
    [InlineData(AccessTechnology.Cellular)]
    [InlineData(AccessTechnology.FixedWireless)]
    public void Usually_metered_technologies_drop_a_rung(AccessTechnology technology)
    {
        var plan = MeteredProbePolicy.For(technology, dataUsageEnabled: false);

        plan.Rung.Should().Be(1);
        plan.MaxAutoEnabled.Should().Be(15);
        plan.PollIntervalSeconds.Should().Be(30);
    }

    [Fact]
    public void A_declared_cap_drops_a_rung_on_its_own()
    {
        // Cable with a cap costs the same per byte as satellite without one.
        var plan = MeteredProbePolicy.For(AccessTechnology.Docsis, dataUsageEnabled: true);

        plan.Rung.Should().Be(1);
        plan.MaxAutoEnabled.Should().Be(15);
    }

    [Fact]
    public void The_two_signals_stack()
    {
        var plan = MeteredProbePolicy.For(AccessTechnology.Satellite, dataUsageEnabled: true);

        plan.Rung.Should().Be(2);
        plan.MaxAutoEnabled.Should().Be(8);
        plan.PollIntervalSeconds.Should().Be(60);
    }

    [Fact]
    public void Rungs_land_where_the_traffic_estimate_says_they_should()
    {
        // The numbers the ladder was chosen against, both directions, 30 days.
        MeteredProbePolicy.EstimatedMonthlyGb(25, 10).Should().BeApproximately(5.44, 0.05);
        MeteredProbePolicy.EstimatedMonthlyGb(15, 30).Should().BeApproximately(1.09, 0.05);
        MeteredProbePolicy.EstimatedMonthlyGb(8, 60).Should().BeApproximately(0.29, 0.02);
    }
}
