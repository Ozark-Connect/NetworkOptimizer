using NetworkOptimizer.Web.Services.Tours;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class TourMergePlannerTests
{
    private static TourDefinition Tour(string version, params TourStep[] steps) => new()
    {
        Id = version,
        Kind = TourKinds.WhatsNew,
        Title = $"What's new in {version}",
        Steps = steps.ToList(),
    };

    private static TourStep Step(string id, string level = TourLevels.Major) => new()
    {
        Id = id,
        Level = level,
        Url = "/",
        Selector = $"[data-tour=\"{id}\"]",
        Title = id,
        Body = id,
    };

    [Fact]
    public void OrdersByReleaseAscendingThenDeclaredOrder()
    {
        var older = Tour("2.4.0", Step("a"), Step("b"));
        var newer = Tour("2.5.0", Step("c"), Step("d"));

        var plan = TourMergePlanner.Build(new[] { older, newer }, (_, _) => true);

        Assert.Equal(new[] { "a", "b", "c", "d" }, plan.Steps.Select(s => s.Step.Id));
        Assert.Equal(0, plan.DroppedCount);
    }

    [Fact]
    public void DeduplicatesByStepIdKeepingNewestCopy()
    {
        var older = Tour("2.4.0", Step("shared"), Step("old-only"));
        var revised = Step("shared");
        revised.Title = "revised";
        var newer = Tour("2.5.0", revised);

        var plan = TourMergePlanner.Build(new[] { older, newer }, (_, _) => true);

        Assert.Equal(2, plan.Steps.Count);
        var shared = plan.Steps.Single(s => s.Step.Id == "shared");
        Assert.Equal("revised", shared.Step.Title);
        Assert.Equal("2.5.0", shared.Tour.Id);
    }

    [Fact]
    public void CapDropsMinorFirstThenOldest()
    {
        // 10 steps: 3 minor scattered across releases, 7 major.
        var t1 = Tour("2.4.0", Step("m1", TourLevels.Minor), Step("a"), Step("b"), Step("c"));
        var t2 = Tour("2.5.0", Step("m2", TourLevels.Minor), Step("d"), Step("e"));
        var t3 = Tour("2.6.0", Step("m3", TourLevels.Minor), Step("f"), Step("g"));

        var plan = TourMergePlanner.Build(new[] { t1, t2, t3 }, (_, _) => true, cap: 8);

        Assert.Equal(8, plan.Steps.Count);
        Assert.Equal(2, plan.DroppedCount);
        // The two oldest minors go first; the newest minor survives.
        Assert.DoesNotContain(plan.Steps, s => s.Step.Id == "m1");
        Assert.DoesNotContain(plan.Steps, s => s.Step.Id == "m2");
        Assert.Contains(plan.Steps, s => s.Step.Id == "m3");
    }

    [Fact]
    public void CapFallsBackToOldestWhenNoMinorsLeft()
    {
        var t1 = Tour("2.4.0", Step("a"), Step("b"), Step("c"), Step("d"), Step("e"));
        var t2 = Tour("2.5.0", Step("f"), Step("g"), Step("h"), Step("i"), Step("j"));

        var plan = TourMergePlanner.Build(new[] { t1, t2 }, (_, _) => true, cap: 8);

        Assert.Equal(8, plan.Steps.Count);
        Assert.Equal(2, plan.DroppedCount);
        Assert.DoesNotContain(plan.Steps, s => s.Step.Id == "a");
        Assert.DoesNotContain(plan.Steps, s => s.Step.Id == "b");
        Assert.Contains(plan.Steps, s => s.Step.Id == "j");
    }

    [Fact]
    public void ExcludedStepsNeverEnterThePlan()
    {
        var tour = Tour("2.4.0", Step("seen"), Step("unseen"), Step("filtered", TourLevels.Advanced));
        var seen = new HashSet<string> { "seen" };

        var plan = TourMergePlanner.Build(
            new[] { tour },
            (_, step) => !seen.Contains(step.Id) && step.Level != TourLevels.Advanced);

        Assert.Single(plan.Steps);
        Assert.Equal("unseen", plan.Steps[0].Step.Id);
        Assert.Equal(0, plan.DroppedCount);
    }

    [Fact]
    public void EmptyInputYieldsEmptyPlan()
    {
        var plan = TourMergePlanner.Build(Array.Empty<TourDefinition>(), (_, _) => true);
        Assert.Empty(plan.Steps);
        Assert.Equal(0, plan.DroppedCount);
    }
}
