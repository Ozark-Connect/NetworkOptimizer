using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The cadence-to-weight fold: continuous agent readings become at most ONE radio-hour, so an
/// agent-covered AP's evidence weighs the same per hour as a console-covered AP's, and a
/// mid-hour channel change cannot smear one channel's airtime onto another.
/// </summary>
public class ApAgentAirtimeAggregatorTests
{
    private const string Ap = "aa:bb:cc:dd:ee:01";
    private static readonly DateTime Hour = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static void RecordMany(ApAgentAirtimeAggregator agg, int count, DateTime from,
        int channel = 6, int width = 40, double util = 50, double interf = 10, string band = "2.4")
    {
        for (var i = 0; i < count; i++)
            agg.Record(Ap, band, channel, width, util, interf, from.AddSeconds(i * 30));
    }

    [Fact]
    public void A_full_hour_of_readings_folds_into_one_hour_with_the_averages()
    {
        var agg = new ApAgentAirtimeAggregator();
        RecordMany(agg, 60, Hour, util: 40, interf: 8);
        RecordMany(agg, 60, Hour.AddMinutes(30), util: 60, interf: 12);

        var hours = agg.GetFinalizedHours(Hour, Hour.AddHours(1));

        var h = hours.Should().ContainSingle().Subject;
        h.ApMac.Should().Be(Ap);
        h.Band.Should().Be("ng");
        h.HourUtc.Should().Be(Hour);
        h.Channel.Should().Be(6);
        h.WidthMhz.Should().Be(40);
        h.ReadingCount.Should().Be(120);
        h.AvgUtilization.Should().BeApproximately(50, 0.001);
        h.AvgInterference.Should().BeApproximately(10, 0.001);
    }

    [Fact]
    public void A_mid_hour_channel_change_attributes_the_hour_to_the_majority_config_only()
    {
        var agg = new ApAgentAirtimeAggregator();
        // 20 minutes on channel 1 at 90% utilization, then 40 minutes on channel 11 at 20%.
        RecordMany(agg, 40, Hour, channel: 1, util: 90, interf: 30);
        RecordMany(agg, 80, Hour.AddMinutes(20), channel: 11, util: 20, interf: 5);

        var h = agg.GetFinalizedHours(Hour, Hour.AddHours(1)).Should().ContainSingle().Subject;

        h.Channel.Should().Be(11, "the config that held the majority of the hour owns it");
        h.ReadingCount.Should().Be(80, "the minority config's readings must not add weight");
        h.AvgUtilization.Should().BeApproximately(20, 0.001, "channel 1's 90% must not bleed into channel 11's average");
        h.AvgInterference.Should().BeApproximately(5, 0.001);
    }

    [Fact]
    public void A_width_change_on_the_same_channel_is_a_distinct_config()
    {
        var agg = new ApAgentAirtimeAggregator();
        RecordMany(agg, 30, Hour, channel: 36, width: 40, util: 80, band: "5");
        RecordMany(agg, 60, Hour.AddMinutes(20), channel: 36, width: 80, util: 20, band: "5");

        var h = agg.GetFinalizedHours(Hour, Hour.AddHours(1)).Should().ContainSingle().Subject;

        h.WidthMhz.Should().Be(80);
        h.AvgUtilization.Should().BeApproximately(20, 0.001);
    }

    [Fact]
    public void Consecutive_hours_finalize_separately()
    {
        var agg = new ApAgentAirtimeAggregator();
        RecordMany(agg, 120, Hour, util: 30);
        RecordMany(agg, 120, Hour.AddHours(1), util: 70);

        var hours = agg.GetFinalizedHours(Hour, Hour.AddHours(2)).OrderBy(h => h.HourUtc).ToList();

        hours.Should().HaveCount(2);
        hours[0].AvgUtilization.Should().BeApproximately(30, 0.001);
        hours[1].AvgUtilization.Should().BeApproximately(70, 0.001);
    }

    [Fact]
    public void Too_few_readings_leave_the_hour_to_the_console_path()
    {
        var agg = new ApAgentAirtimeAggregator();
        RecordMany(agg, ApAgentAirtimeAggregator.MinReadingsPerHour - 1, Hour);

        agg.GetFinalizedHours(Hour, Hour.AddHours(1)).Should().BeEmpty(
            "a coverage blip must not displace the console's full-hour aggregate");
    }

    [Fact]
    public void An_agent_that_dies_mid_hour_still_surrenders_its_partial_hour_on_read()
    {
        var agg = new ApAgentAirtimeAggregator();
        // Coverage stops 10 minutes in; no later reading arrives to roll the hour over.
        RecordMany(agg, 20, Hour, util: 55);

        var h = agg.GetFinalizedHours(Hour, Hour.AddHours(1)).Should().ContainSingle().Subject;

        h.ReadingCount.Should().Be(20);
        h.AvgUtilization.Should().BeApproximately(55, 0.001);
    }

    [Fact]
    public void The_in_progress_hour_is_not_finalized_early()
    {
        var agg = new ApAgentAirtimeAggregator();
        RecordMany(agg, 120, Hour);

        agg.GetFinalizedHours(Hour, Hour.AddMinutes(30)).Should().BeEmpty(
            "an hour is only comparable to a console hourly row once it has fully elapsed");

        // The readings are still held, not lost: the next sweep's later boundary collects them.
        agg.GetFinalizedHours(Hour, Hour.AddHours(1)).Should().ContainSingle();
    }

    [Fact]
    public void Hours_survive_a_failed_sweep_and_are_gone_after_the_commit_prune()
    {
        var agg = new ApAgentAirtimeAggregator();
        RecordMany(agg, 120, Hour);

        agg.GetFinalizedHours(Hour, Hour.AddHours(1)).Should().ContainSingle("a read must not consume");
        agg.GetFinalizedHours(Hour, Hour.AddHours(1)).Should().ContainSingle();

        agg.PruneBefore(Hour.AddHours(1));
        agg.GetFinalizedHours(Hour, Hour.AddHours(1)).Should().BeEmpty(
            "a committed hour must never be handed to a second sweep");
    }

    [Theory]
    [InlineData("2.4", "ng")]
    [InlineData("5", "na")]
    [InlineData("6", "6e")]
    [InlineData("ng", "ng")]
    [InlineData("na", "na")]
    [InlineData("6e", "6e")]
    public void Band_tokens_map_to_the_outcome_table_codes(string token, string code)
        => ApAgentAirtimeAggregator.MapBandCode(token).Should().Be(code);

    [Fact]
    public void Unknown_band_absent_channel_and_sentinel_utilization_are_dropped()
    {
        var agg = new ApAgentAirtimeAggregator();
        for (var i = 0; i < 120; i++)
        {
            var at = Hour.AddSeconds(i * 30);
            agg.Record(Ap, "radar", 6, 40, 50, 10, at);
            agg.Record(Ap, "2.4", 0, 40, 50, 10, at);
            agg.Record(Ap, "2.4", 6, 40, 4294967295, 10, at);
        }

        agg.GetFinalizedHours(Hour, Hour.AddHours(1)).Should().BeEmpty();
    }

    [Fact]
    public void Old_hours_age_out_of_retention()
    {
        var agg = new ApAgentAirtimeAggregator();
        RecordMany(agg, 120, Hour.AddDays(-8));
        RecordMany(agg, 120, Hour);

        var hours = agg.GetFinalizedHours(DateTime.MinValue, Hour.AddHours(1));

        hours.Should().ContainSingle().Which.HourUtc.Should().Be(Hour);
    }
}
