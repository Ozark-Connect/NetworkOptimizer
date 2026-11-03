using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathLocalization;

/// <summary>
/// The gate (spec 3.3) against the events it was written from: an evening of us-east and ORD degradation
/// passes, a site-wide loss that takes out the access layer does not.
/// </summary>
public class PathEpisodeGateTests
{
    private static GateTarget Path(string id, bool degraded, double loss = 0, double rise = 0) =>
        new(id, GateRole.Path, degraded, !degraded, loss, rise);

    private static GateTarget Access(string id, bool degraded) => new(id, GateRole.Access, degraded, !degraded, 0, 0);

    private static readonly GateTarget[] Evening =
    {
        Access("bng", false),
        Access("border", false),
        new("cox-transit", GateRole.Other, false, true, 0, 0),
        Path("aws-us-east-1", true, 13, 13),
        Path("aws-us-east-2", true, 5, 13),
        Path("speedtest-ord", true, 12, 40),
        Path("speedtest-den", false),
        Path("aws-us-west-1", false),
        Path("aws-us-west-2", false),
        Path("cloudflare", false),
        Path("google", false),
    };

    [Fact]
    public void Evening_PassesWithWorstFirst()
    {
        var r = PathEpisodeGate.Evaluate(Evening, utilization: 0.1, loadHighFraction: 0.5, outageOpen: false);

        r.Passed.Should().BeTrue();
        r.Degraded.Select(t => t.TargetId).Should().Equal("aws-us-east-1", "speedtest-ord", "aws-us-east-2");
        r.CleanControls.Should().HaveCount(5);
    }

    [Fact]
    public void SiteWideLoss_FailsOnTheAccessLayer()
    {
        var targets = Evening.Select(t => t with { Degraded = true, Clean = false }).ToList();
        var r = PathEpisodeGate.Evaluate(targets, null, 0.5, false);

        r.Passed.Should().BeFalse();
        r.BlockedBy.Should().Contain("access layer");
    }

    [Fact]
    public void HalfOrMoreDegraded_ReadsAsSiteWide()
    {
        var targets = new[] { Path("a", true), Path("b", true), Path("c", false), Path("d", false) };
        PathEpisodeGate.Evaluate(targets, null, 0.5, false).BlockedBy.Should().Contain("site-wide");
    }

    [Fact]
    public void NoCleanControl_Blocks()
    {
        // Three degraded of seven, but the four others have no baseline yet (not degraded, not clean).
        var targets = new[]
        {
            Path("a", true), Path("b", true), Path("c", true),
            new GateTarget("d", GateRole.Path, false, false, 0, 0), new GateTarget("e", GateRole.Path, false, false, 0, 0),
            new GateTarget("f", GateRole.Path, false, false, 0, 0), new GateTarget("g", GateRole.Path, false, false, 0, 0),
        };
        PathEpisodeGate.Evaluate(targets, null, 0.5, false).BlockedBy.Should().Contain("no clean control");

        // An episode already open keeps tracing: after a restart nothing has a judged window yet.
        PathEpisodeGate.Evaluate(targets, null, 0.5, false, requireCleanControl: false).Passed.Should().BeTrue();
    }

    [Fact]
    public void LoadedWan_Blocks_UnknownLoadDoesNot()
    {
        PathEpisodeGate.Evaluate(Evening, 0.8, 0.5, false).BlockedBy.Should().Contain("loaded");
        PathEpisodeGate.Evaluate(Evening, null, 0.5, false).Passed.Should().BeTrue();
    }

    // Starlink, an evening swing as it opens: two of six targets have strung three windows together, the
    // access hop and the rest are over their lines on and off.
    private static GateTarget[] Starlink(bool accessElevated = false, double accessRise = 0, bool othersElevated = false) =>
    [
        new("cgnat", GateRole.Access, false, !accessElevated, 0, accessRise, accessElevated),
        Path("cloudflare", true, 0, 20) with { Elevated = true },
        Path("quad9", true, 0, 18) with { Elevated = true },
        Path("microsoft", false) with { Elevated = othersElevated },
        Path("google", false),
        Path("apple", false),
        Path("opendns", false),
    ];

    [Fact]
    public void AccessHopThatRoseRecently_Blocks_ButNotAnOpenEpisode()
    {
        PathEpisodeGate.Evaluate(Starlink(accessElevated: true), null, 0.5, false).BlockedBy.Should().Contain("access hop rose recently");
        PathEpisodeGate.Evaluate(Starlink(accessElevated: true), null, 0.5, false, requireCleanControl: false).Passed.Should().BeTrue();
    }

    [Fact]
    public void AccessHopThatRoseWithTheTargets_Blocks()
    {
        // Under its own line, but up 12 ms against the degraded targets' 19.
        PathEpisodeGate.Evaluate(Starlink(accessRise: 12), null, 0.5, false).BlockedBy.Should().Contain("rose with the degraded targets");
        PathEpisodeGate.Evaluate(Starlink(accessRise: 4), null, 0.5, false).Passed.Should().BeTrue();
    }

    [Fact]
    public void HalfThePathTargetsRisingRecently_ReadsAsWanWide()
    {
        PathEpisodeGate.Evaluate(Starlink(othersElevated: true), null, 0.5, false).BlockedBy.Should().Contain("WAN-wide");
    }

    [Fact]
    public void OpenOutage_Blocks()
    {
        PathEpisodeGate.Evaluate(Evening, 0.1, 0.5, true).BlockedBy.Should().Contain("outage");
    }
}
