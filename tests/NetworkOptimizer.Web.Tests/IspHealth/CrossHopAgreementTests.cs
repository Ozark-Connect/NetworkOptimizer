using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Congestion on a link is common to everything crossing it. One hop rising while the hops beside
/// it stay flat AT THE SAME SECOND is that hop's own responder deprioritizing ICMP, and the flat
/// readings taken alongside it are the proof - proof the old flat pooling threw away, because the
/// noise floor discarded the clean samples before the median ever saw them.
/// </summary>
public class CrossHopAgreementTests
{
    private static readonly DateTime T0 = new(2026, 8, 5, 16, 23, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    private static (DateTime, double, int) S(double atSecond, double value, int hop) =>
        (T0.AddSeconds(atSecond), value, hop);

    [Fact]
    public void One_squealing_hop_is_outvoted_by_its_clean_neighbors()
    {
        var samples = new[]
        {
            S(0, 0.1, 0), S(0.2, 8.0, 1), S(0.4, 0.2, 2), S(0.6, 0.1, 3), S(0.8, 0.0, 4),
        };

        var agreed = SeriesStats.CommonModeByInstant(samples, Tolerance, minCohort: 2);

        agreed.Should().HaveCount(1);
        agreed[0].Value.Should().BeApproximately(0.1, 0.001);
    }

    [Fact]
    public void A_link_that_is_genuinely_loaded_carries_every_hop_up_together()
    {
        var samples = new[]
        {
            S(0, 21.0, 0), S(0.2, 24.0, 1), S(0.4, 22.0, 2), S(0.6, 23.0, 3),
        };

        var agreed = SeriesStats.CommonModeByInstant(samples, Tolerance, minCohort: 2);

        agreed.Should().HaveCount(1);
        agreed[0].Value.Should().BeApproximately(22.5, 0.001);
    }

    [Fact]
    public void A_hop_with_nothing_beside_it_is_passed_through_untouched()
    {
        // Short events where only one hop happened to be probed are still evidence. Uncorroborated
        // evidence is not the same as refuted evidence, and dropping it would blind the score to
        // exactly the brief spikes it is supposed to notice.
        var samples = new[] { S(0, 9.0, 0) };

        var agreed = SeriesStats.CommonModeByInstant(samples, Tolerance, minCohort: 2);

        agreed.Should().ContainSingle().Which.Value.Should().Be(9.0);
    }

    [Fact]
    public void Two_readings_from_the_SAME_hop_do_not_corroborate_each_other()
    {
        var samples = new[] { S(0, 9.0, 0), S(0.3, 9.2, 0) };

        var agreed = SeriesStats.CommonModeByInstant(samples, Tolerance, minCohort: 2);

        agreed.Should().HaveCount(2);
        agreed.Select(a => a.Value).Should().BeEquivalentTo(new[] { 9.0, 9.2 });
    }

    [Fact]
    public void Samples_further_apart_than_the_tolerance_are_separate_instants()
    {
        // Not simultaneous, so they say nothing about each other: a hop that was clean five
        // seconds later does not testify about the second the spike happened.
        var samples = new[] { S(0, 8.0, 0), S(5, 0.1, 1) };

        var agreed = SeriesStats.CommonModeByInstant(samples, Tolerance, minCohort: 2);

        agreed.Should().HaveCount(2);
        agreed.Select(a => a.Value).Should().BeEquivalentTo(new[] { 8.0, 0.1 });
    }

    [Fact]
    public void Instants_are_reported_in_time_order_regardless_of_input_order()
    {
        var samples = new[] { S(10, 1.0, 0), S(10.2, 1.2, 1), S(0, 5.0, 0), S(0.2, 5.4, 1) };

        var agreed = SeriesStats.CommonModeByInstant(samples, Tolerance, minCohort: 2);

        agreed.Should().HaveCount(2);
        agreed[0].Time.Should().BeBefore(agreed[1].Time);
    }
}
