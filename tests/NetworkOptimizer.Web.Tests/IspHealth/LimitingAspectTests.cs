using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// The ASN cards and hop rows have room for one of the two score-only aspects, so the picker
/// has to choose the one actually holding the grade down.
/// </summary>
public class LimitingAspectTests
{
    private const double StabilityWeight = 0.25;
    private const double CongestionWeight = 0.2;

    private static (string Label, string Value, int? Score)? Pick(
        int? stability, int? congestion, int events) =>
        IspHealthPresentation.LimitingAspect(stability, congestion, events, StabilityWeight, CongestionWeight);

    [Fact]
    public void Congestion_wins_when_it_costs_the_grade_more()
    {
        var pick = Pick(stability: 100, congestion: 45, events: 3);

        Assert.Equal("Congestion", pick!.Value.Label);
        Assert.Equal("3 Events", pick.Value.Value);
        Assert.Equal(45, pick.Value.Score);
    }

    [Fact]
    public void A_single_event_reads_singular()
    {
        Assert.Equal("1 Event", Pick(stability: 100, congestion: 45, events: 1)!.Value.Value);
    }

    [Fact]
    public void Stability_wins_when_it_costs_the_grade_more()
    {
        var pick = Pick(stability: 39, congestion: 95, events: 1);

        Assert.Equal("Stability", pick!.Value.Label);
        Assert.Equal("39", pick.Value.Value);
        Assert.Equal(39, pick.Value.Score);
    }

    [Fact]
    public void Clean_congestion_yields_the_slot_to_stability()
    {
        // Both perfect: "0 Events" says less than the number, so the number wins.
        var pick = Pick(stability: 100, congestion: 100, events: 0);

        Assert.Equal("Stability", pick!.Value.Label);
        Assert.Equal("100", pick.Value.Value);
    }

    [Fact]
    public void Clean_congestion_still_shows_when_stability_is_unavailable()
    {
        var pick = Pick(stability: null, congestion: 100, events: 0);

        Assert.Equal("Congestion", pick!.Value.Label);
        Assert.Equal("0 Events", pick.Value.Value);
    }

    [Fact]
    public void Nothing_scored_shows_nothing()
    {
        Assert.Null(Pick(stability: null, congestion: null, events: 0));
    }
}
