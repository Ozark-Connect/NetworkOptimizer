using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;
using Xunit;
using Q = NetworkOptimizer.Web.Services.Monitoring.LiveTraffic.LiveTrafficPolicyResolver.FlowQuery;
using V = NetworkOptimizer.Web.Services.Monitoring.LiveTraffic.LiveTrafficPolicyResolver.PolicyVerdict;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>Which evaluated rule a live flow shows, given the verdicts for it (and its reverse, for LAN to LAN).</summary>
public class LiveTrafficPolicyResolverTests
{
    private static readonly Q Out = new("192.0.2.20", null, 50000, "192.0.2.10", 445, "tcp");
    private static readonly Q Back = new("192.0.2.10", null, 445, "192.0.2.20", 50000, "tcp");

    [Fact]
    public void AnAllowingRuleNamesTheFlow() =>
        LiveTrafficPolicyResolver.Name(new Dictionary<Q, V> { [Out] = new("[NAS] Upstream DoH", true) }, Out, null)
            .Should().Be("[NAS] Upstream DoH");

    [Fact]
    public void ABlockVerdictForALiveFlowNamesNothing() =>
        LiveTrafficPolicyResolver.Name(new Dictionary<Q, V> { [Out] = new("Block DoH", false) }, Out, null)
            .Should().BeNull("the flow got through, so our model of the config is off");

    [Fact]
    public void TheOneAllowedDirectionNamesALanFlow() =>
        LiveTrafficPolicyResolver.Name(new Dictionary<Q, V> { [Out] = new("Block IoT", false), [Back] = new("Allow Server to IoT", true) }, Out, Back)
            .Should().Be("Allow Server to IoT");

    [Fact]
    public void TwoDifferentAllowedDirectionsNameNothing() =>
        LiveTrafficPolicyResolver.Name(new Dictionary<Q, V> { [Out] = new("Allow A", true), [Back] = new("Allow B", true) }, Out, Back)
            .Should().BeNull("which end opened the flow is unknown");

    [Fact]
    public void BothDirectionsOnOneRuleNameIt() =>
        LiveTrafficPolicyResolver.Name(new Dictionary<Q, V> { [Out] = new("Allow All Traffic", true), [Back] = new("Allow All Traffic", true) }, Out, Back)
            .Should().Be("Allow All Traffic");
}
