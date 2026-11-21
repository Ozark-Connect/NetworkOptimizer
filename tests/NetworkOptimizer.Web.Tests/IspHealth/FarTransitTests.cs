using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

public class FarTransitTests
{
    private static FarTransit.Candidate Near(string id, double floor, string wan = "wan") => new(id, wan, floor, false);
    private static FarTransit.Candidate WasFar(string id, double? floor, string wan = "wan") => new(id, wan, floor, true);

    [Fact]
    public void ADistantPop_IsFar_AndRegionalHopsAreNot()
    {
        // A handoff two time zones away beside nine regional hops.
        var far = FarTransit.Classify([
            Near("distant", 49.5), Near("r1", 13.0), Near("r2", 16.0), Near("r3", 23.5), Near("r4", 9.5),
            Near("r5", 14.1), Near("r6", 7.0), Near("r7", 17.3), Near("r8", 10.8), Near("r9", 15.1)]);

        far.Should().BeEquivalentTo(["distant"]);
    }

    [Fact]
    public void TheNextPopAfterHandoff_IsNotFar()
    {
        // The next POP sits 11 ms above a metro median of 3.6 ms.
        var far = FarTransit.Classify([
            Near("m1", 3.55), Near("m2", 3.91), Near("next-pop", 14.6), Near("m3", 4.52),
            Near("m4", 2.68), Near("m5", 2.14), Near("m6", 2.58)]);

        far.Should().BeEmpty();
    }

    [Fact]
    public void ALocalExchange_DoesNotMakeTheFirstPopFar()
    {
        // A 1.8 ms exchange point beside a 17 ms first POP: anchored on the lowest floor, every POP hop would read far.
        var far = FarTransit.Classify([
            Near("pop-1", 17.2), Near("pop-2", 26.9), Near("pop-3", 22.2), Near("pop-4", 17.2),
            Near("ix-1", 1.83), Near("ix-2", 1.85)]);

        far.Should().BeEmpty();
    }

    [Fact]
    public void FewerThanThreeMeasuredTargets_NothingIsFar()
    {
        var far = FarTransit.Classify([WasFar("distant", 49.5), Near("r1", 13.0)]);

        far.Should().BeEmpty();
    }

    [Fact]
    public void AFarTarget_StaysFarUntilBelowTheNearLine()
    {
        // 17 ms above a median of 10: under the far line, over the near line.
        FarTransit.Candidate[] pool(bool wasFar) => [new("x", "wan", 27, wasFar), Near("a", 10), Near("b", 10), Near("c", 9)];

        FarTransit.Classify(pool(wasFar: true)).Should().BeEquivalentTo(["x"]);
        FarTransit.Classify(pool(wasFar: false)).Should().BeEmpty();
    }

    [Fact]
    public void ATargetWithNoFloor_KeepsItsLastVerdict()
    {
        var far = FarTransit.Classify([WasFar("distant", null), Near("a", 10), Near("b", 11), Near("c", 12)]);

        far.Should().BeEquivalentTo(["distant"]);
    }

    [Fact]
    public void EachWan_IsJudgedAgainstItsOwnTargets()
    {
        // A high-latency second WAN: its hops are far from the first WAN's, not from each other.
        var far = FarTransit.Classify([
            Near("a", 10), Near("b", 11), Near("c", 12),
            Near("lte-a", 60, "wan2"), Near("lte-b", 62, "wan2"), Near("lte-c", 65, "wan2")]);

        far.Should().BeEmpty();
    }

    [Fact]
    public void AFloor_NeedsFifteenMinutesOfPoints()
    {
        FarTransit.Floor(Enumerable.Repeat(10.0, FarTransit.MinFloorPoints - 1).ToList()).Should().BeNull();
        FarTransit.Floor(Enumerable.Repeat(10.0, FarTransit.MinFloorPoints).ToList()).Should().Be(10);
    }

    [Fact]
    public void AnUnpinnedTarget_GroupsWithThePrimary()
    {
        FarTransit.WanKeyOf("unpinned").Should().Be(FarTransit.WanKeyOf("wan"));
        FarTransit.WanKeyOf(null).Should().Be(FarTransit.WanKeyOf("wan"));
    }
}
