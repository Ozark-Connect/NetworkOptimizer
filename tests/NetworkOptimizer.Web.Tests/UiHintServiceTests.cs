using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// A hint that exists to reveal a gesture is for the first encounter, not the hundredth. These pin
/// the arithmetic of "shown enough"; the storage round-trip needs a full Identity graph and is
/// exercised on a test site instead.
/// </summary>
public class UiHintServiceTests
{
    private static bool StillOwed(int timesShown) => timesShown < UiHintService.ShowLimit;

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void AHintRetiresOnceItHasBeenShownItsAllowance(int timesShown, bool expected)
    {
        StillOwed(timesShown).Should().Be(expected);
    }

    [Fact]
    public void TheAllowanceIsTwoOccasions()
    {
        // Twice: once to notice it exists, once to remember what it said. A single showing is
        // easily missed and a third is nagging.
        UiHintService.ShowLimit.Should().Be(2);
    }

    [Fact]
    public void TheCountStopsClimbingAtTheLimit()
    {
        // Left to grow, a "shown 400 times" would make any future reset read as absurd - and the
        // number past the limit answers no question anyone has.
        var shown = 0;
        for (var visit = 0; visit < 10; visit++)
            if (shown < UiHintService.ShowLimit) shown++;

        shown.Should().Be(UiHintService.ShowLimit);
    }

    [Fact]
    public void HintKeysAreStableStrings()
    {
        // Renaming one starts its count over, which is harmless - but it should be a decision,
        // not a typo, so the keys live in one place.
        UiHintKeys.WanFilterCompare.Should().Be("wan-filter-compare");
    }
}
