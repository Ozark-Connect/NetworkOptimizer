using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The shape half of the WAN outage alert family: given one WAN's current target states, which
/// outage the picture is. Pure and stateless, so these cover every branch directly - the two
/// total shapes (access layer down, and the first hop answering while everything beyond it is
/// dark), the branch-shaped and independent partials, and the deliberate silences that keep the
/// alert class quiet: one dark destination, a transit hop nobody sits behind, and too little
/// evidence to say anything at all.
/// </summary>
public class WanOutageClassifierTests
{
    private const string AccessIp = "192.0.2.1";
    private const string TransitIp = "198.51.100.1";
    private const string SecondTransitIp = "198.51.100.2";

    private static WanTargetSnapshot Access(bool failing = false, bool degraded = false) =>
        new("wan-access", MonitoringTargetType.AccessIsp, "Acme Fiber first hop", AccessIp,
            Failing: failing, Degraded: failing || degraded, Depth: 1, KnownPosition: true,
            IsInternet: false, AsnLabel: "Acme Fiber", AsnNumber: 64500,
            AncestorIps: Ancestors(null));

    private static WanTargetSnapshot Transit(string id, string address, int depth,
        bool failing = false, bool degraded = false, string? asnLabel = "TransitNet",
        int asnNumber = 64501, string[]? ancestors = null) =>
        new(id, MonitoringTargetType.Transit, id, address,
            Failing: failing, Degraded: failing || degraded, Depth: depth, KnownPosition: true,
            IsInternet: false, AsnLabel: asnLabel, AsnNumber: asnNumber,
            AncestorIps: Ancestors(ancestors));

    private static WanTargetSnapshot Internet(string id, string address,
        bool failing = false, bool degraded = false, string? asnLabel = "Alpha Cloud",
        int asnNumber = 64510, string[]? ancestors = null) =>
        new(id, MonitoringTargetType.InternetService, id, address,
            Failing: failing, Degraded: failing || degraded, Depth: 6, KnownPosition: true,
            IsInternet: true, AsnLabel: asnLabel, AsnNumber: asnNumber,
            AncestorIps: Ancestors(ancestors));

    private static IReadOnlySet<string> Ancestors(string[]? ips) =>
        new HashSet<string>(ips ?? [], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Classify_NoTargets_ReturnsNone()
    {
        var verdict = WanOutageClassifier.Classify([]);

        verdict.Kind.Should().Be(WanVerdictKind.None);
        verdict.Should().BeSameAs(WanVerdict.None);
    }

    [Fact]
    public void Classify_EveryTargetFailingWithAnAccessHop_IsTotalWithTheAccessLayerDown()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(failing: true),
            Transit("transit-a", TransitIp, 3, failing: true, ancestors: [AccessIp]),
            Internet("resolver-a", "203.0.113.10", failing: true, ancestors: [AccessIp, TransitIp])
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.Total);
        verdict.AccessDown.Should().BeTrue();
        verdict.FailingCount.Should().Be(3);
        verdict.TotalCount.Should().Be(3);
    }

    /// <summary>
    /// Same picture without a monitored first hop: still the connection, but nothing to say the
    /// access layer itself is the part that went - so no attribution is claimed.
    /// </summary>
    [Fact]
    public void Classify_EveryTargetFailingWithNoAccessHopMonitored_IsTotalWithoutAttribution()
    {
        var verdict = WanOutageClassifier.Classify([
            Transit("transit-a", TransitIp, 3, failing: true),
            Internet("resolver-a", "203.0.113.10", failing: true, ancestors: [TransitIp]),
            Internet("resolver-b", "203.0.113.20", failing: true, asnLabel: "Beta Cloud",
                asnNumber: 64520, ancestors: [TransitIp])
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.Total);
        verdict.AccessDown.Should().BeFalse();
        verdict.LastReachableHop.Should().BeNull();
        verdict.BrokenNetwork.Should().BeNull();
    }

    [Fact]
    public void Classify_FirstHopAnswersAndEverythingBeyondFails_IsTotalAttributedToTheFirstHop()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(),
            Transit("transit-a", TransitIp, 3, failing: true, ancestors: [AccessIp]),
            Internet("resolver-a", "203.0.113.10", failing: true, ancestors: [AccessIp, TransitIp]),
            Internet("resolver-b", "203.0.113.20", failing: true, asnLabel: "Beta Cloud",
                asnNumber: 64520, ancestors: [AccessIp, TransitIp])
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.Total);
        verdict.AccessDown.Should().BeFalse();
        verdict.LastReachableHop.Should().Be("Acme Fiber");
        verdict.FailingCount.Should().Be(3);
        verdict.TotalCount.Should().Be(4);
    }

    /// <summary>
    /// A transit hop dark WITH a destination behind it also dark is the corroborated branch: the
    /// hop is the branch head, and it names the partial.
    /// </summary>
    [Fact]
    public void Classify_TransitHopAndTheDestinationBehindItFailing_IsPartialNamingTheTransit()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(),
            Transit("transit-a", TransitIp, 3, failing: true, ancestors: [AccessIp]),
            Internet("resolver-a", "203.0.113.10", failing: true, ancestors: [AccessIp, TransitIp]),
            Internet("resolver-b", "203.0.113.20", asnLabel: "Beta Cloud", asnNumber: 64520,
                ancestors: [AccessIp])
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.Partial);
        verdict.BranchLabel.Should().Be("TransitNet");
        verdict.FailingCount.Should().Be(2);
        verdict.TotalCount.Should().Be(4);
    }

    /// <summary>
    /// The break just past a hop that still answers: every failing destination sits behind the
    /// transit and no reachable one does, so the transit is where the picture narrows.
    /// </summary>
    [Fact]
    public void Classify_DestinationsSharingAReachableAncestor_IsPartialNamingThatAncestor()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(),
            Transit("transit-a", TransitIp, 3, ancestors: [AccessIp]),
            Internet("resolver-a", "203.0.113.10", failing: true, ancestors: [AccessIp, TransitIp]),
            Internet("resolver-b", "203.0.113.20", failing: true, asnLabel: "Beta Cloud",
                asnNumber: 64520, ancestors: [AccessIp, TransitIp])
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.Partial);
        verdict.BranchLabel.Should().Be("TransitNet");
    }

    /// <summary>
    /// The ancestor healthy traffic also crosses cannot be the branch - naming the first hop
    /// while other destinations behind it are perfectly reachable would blame the wrong network.
    /// </summary>
    [Fact]
    public void Classify_AncestorHealthyTrafficAlsoCrosses_IsPartialWithoutABranch()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(),
            Internet("resolver-a", "203.0.113.10", failing: true, ancestors: [AccessIp]),
            Internet("resolver-b", "203.0.113.20", failing: true, asnLabel: "Beta Cloud",
                asnNumber: 64520, ancestors: [AccessIp]),
            Internet("resolver-c", "203.0.113.30", asnLabel: "Gamma Cloud", asnNumber: 64530,
                ancestors: [AccessIp])
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.Partial);
        verdict.BranchLabel.Should().BeNull();
        verdict.FailingCount.Should().Be(2);
    }

    [Fact]
    public void Classify_UnrelatedDestinationsFailing_IsPartialWithoutABranch()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(),
            Internet("resolver-a", "203.0.113.10", failing: true, ancestors: [TransitIp]),
            Internet("resolver-b", "203.0.113.20", failing: true, asnLabel: "Beta Cloud",
                asnNumber: 64520, ancestors: [SecondTransitIp]),
            Internet("resolver-c", "203.0.113.30", asnLabel: "Gamma Cloud", asnNumber: 64530)
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.Partial);
        verdict.BranchLabel.Should().BeNull();
    }

    /// <summary>
    /// Several endpoints of one provider are one network, not several independent ones, so with
    /// no monitored hop to anchor on the network itself is what the partial is named after.
    /// </summary>
    [Fact]
    public void Classify_TwoDestinationsOfOneUnlabeledAsn_IsPartialNamingTheAsn()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(),
            Internet("resolver-a", "203.0.113.10", failing: true, asnLabel: null, asnNumber: 64540),
            Internet("resolver-b", "203.0.113.20", failing: true, asnLabel: null, asnNumber: 64540),
            Internet("resolver-c", "203.0.113.30", asnLabel: "Gamma Cloud", asnNumber: 64530)
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.Partial);
        verdict.BranchLabel.Should().Be("AS64540");
    }

    /// <summary>
    /// One dark destination is nearly always the destination's own problem, and alerting on it
    /// would rebuild exactly the per-target noise this alert class exists to remove.
    /// </summary>
    [Fact]
    public void Classify_OneDestinationFailing_ReturnsNone()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(),
            Transit("transit-a", TransitIp, 3, ancestors: [AccessIp]),
            Internet("resolver-a", "203.0.113.10", failing: true, ancestors: [AccessIp, TransitIp]),
            Internet("resolver-b", "203.0.113.20", asnLabel: "Beta Cloud", asnNumber: 64520,
                ancestors: [AccessIp, TransitIp])
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.None);
    }

    [Fact]
    public void Classify_OneTransitHopFailingAlone_ReturnsNone()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(),
            Transit("transit-a", TransitIp, 3, failing: true, ancestors: [AccessIp]),
            Internet("resolver-a", "203.0.113.10", ancestors: [AccessIp, TransitIp])
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.None);
    }

    /// <summary>
    /// Transit routers rate-limit ICMP with nothing wrong, so a transit-only picture with every
    /// destination still reachable is never an outage however many hops stop answering.
    /// </summary>
    [Fact]
    public void Classify_TransitHopsDarkWithEveryDestinationReachable_ReturnsNone()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(),
            Transit("transit-a", TransitIp, 3, failing: true, ancestors: [AccessIp]),
            Transit("transit-b", SecondTransitIp, 4, failing: true, asnLabel: "Delta Transit",
                asnNumber: 64502, ancestors: [AccessIp]),
            Internet("resolver-a", "203.0.113.10", ancestors: [AccessIp, TransitIp]),
            Internet("resolver-b", "203.0.113.20", asnLabel: "Beta Cloud", asnNumber: 64520,
                ancestors: [AccessIp, SecondTransitIp])
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.None);
    }

    /// <summary>
    /// Sustained loss counts toward a partial without the target ever going fully dark - a branch
    /// can be out or degraded without every probe failing outright.
    /// </summary>
    [Fact]
    public void Classify_DegradedButNotOfflineDestinations_IsPartial()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(),
            Internet("resolver-a", "203.0.113.10", degraded: true, ancestors: [TransitIp]),
            Internet("resolver-b", "203.0.113.20", degraded: true, asnLabel: "Beta Cloud",
                asnNumber: 64520, ancestors: [SecondTransitIp]),
            Internet("resolver-c", "203.0.113.30", asnLabel: "Gamma Cloud", asnNumber: 64530)
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.Partial);
        verdict.BranchLabel.Should().BeNull();
        verdict.FailingCount.Should().Be(2);
    }

    /// <summary>
    /// Degraded is not offline: a WAN losing packets everywhere still passes traffic, so however
    /// wide the degradation it must not read as the connection being down.
    /// </summary>
    [Fact]
    public void Classify_EveryTargetDegradedButNoneFailing_IsNeverTotal()
    {
        var verdict = WanOutageClassifier.Classify([
            Access(degraded: true),
            Transit("transit-a", TransitIp, 3, degraded: true, ancestors: [AccessIp]),
            Internet("resolver-a", "203.0.113.10", degraded: true, ancestors: [AccessIp, TransitIp]),
            Internet("resolver-b", "203.0.113.20", degraded: true, asnLabel: "Beta Cloud",
                asnNumber: 64520, ancestors: [AccessIp, TransitIp])
        ]);

        verdict.Kind.Should().Be(WanVerdictKind.Partial);
        verdict.AccessDown.Should().BeFalse();
    }
}
