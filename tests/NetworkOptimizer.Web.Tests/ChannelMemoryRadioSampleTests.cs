using FluentAssertions;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The channel memory sweep's per-radio sample builder: an AP Agent radio-hour replaces the
/// console's sample for that hour instead of joining it (SampleCount is a weight in a decay
/// model, so a double-written hour would silently bias the recommendation), and an AP or hour
/// the agent did not cover falls back to the console path with no gap.
/// </summary>
public class ChannelMemoryRadioSampleTests
{
    private const string Ap = "aa:bb:cc:dd:ee:01";
    private const string Band = "ng";

    private static readonly DateTimeOffset Start = new(2026, 8, 24, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static SiteWiFiMetrics Metric(int hour, double util = 30, double interf = 5, double? txRetry = 2) =>
        new()
        {
            Timestamp = new DateTimeOffset(2026, 8, 24, hour, 0, 0, TimeSpan.Zero),
            ByBand = new Dictionary<RadioBand, BandMetrics>
            {
                [RadioBand.Band2_4GHz] = new()
                {
                    ChannelUtilization = util,
                    Interference = interf,
                    TxRetryPct = txRetry,
                },
            },
        };

    private static ApAgentAirtimeHour AgentHour(int hour, int channel = 6, int width = 40,
        double util = 44, double interf = 11, string apMac = Ap, string band = Band) =>
        new(apMac, band, new DateTime(2026, 8, 24, hour, 0, 0, DateTimeKind.Utc),
            channel, width, util, interf, 120,
            new DateTime(2026, 8, 24, hour, 59, 30, DateTimeKind.Utc));

    private static List<ChannelOutcomeSample> Build(
        List<SiteWiFiMetrics> metrics,
        IReadOnlyList<ApAgentAirtimeHour> agentHours,
        List<ChannelChangeEvent>? events = null,
        int currentChannel = 6,
        int currentWidth = 40) =>
        ChannelMemoryCollectionService.BuildRadioSamples(
            Ap, Band, RadioBand.Band2_4GHz, metrics, events ?? new List<ChannelChangeEvent>(),
            currentChannel, currentWidth, widthValidFrom: DateTimeOffset.MinValue,
            Start, End, agentHours);

    [Fact]
    public void Without_agent_hours_the_console_path_is_unchanged()
    {
        var samples = Build(new List<SiteWiFiMetrics> { Metric(6), Metric(7) }, Array.Empty<ApAgentAirtimeHour>());

        samples.Should().HaveCount(2);
        samples.Should().OnlyContain(s => s.Channel == 6 && s.Utilization == 30 && s.TxRetryPct == 2);
    }

    [Fact]
    public void An_agent_hour_replaces_the_console_sample_for_that_hour()
    {
        var samples = Build(
            new List<SiteWiFiMetrics> { Metric(6), Metric(7) },
            new[] { AgentHour(7) });

        samples.Should().HaveCount(2, "one sample per radio-hour, whichever source wins");
        samples.Should().ContainSingle(s => s.TimestampUtc.Hour == 6 && s.Utilization == 30,
            "hour 6 stays console-sourced");
        var agentSample = samples.Should().ContainSingle(s => s.TimestampUtc.Hour == 7).Subject;
        agentSample.Utilization.Should().Be(44);
        agentSample.Interference.Should().Be(11);
        agentSample.WidthMhz.Should().Be(40);
    }

    [Fact]
    public void An_agent_covered_hour_never_yields_two_samples()
    {
        var samples = Build(
            new List<SiteWiFiMetrics> { Metric(6), Metric(7), Metric(8) },
            new[] { AgentHour(6), AgentHour(7), AgentHour(8) });

        samples.Should().HaveCount(3);
        samples.Should().OnlyContain(s => s.Utilization == 44, "every covered hour is agent-sourced exactly once");
    }

    [Fact]
    public void An_agent_hour_with_no_console_metric_still_lands()
    {
        // The console kept nothing for these hours - the evidence-scarcity case this exists for.
        var samples = Build(new List<SiteWiFiMetrics>(), new[] { AgentHour(9), AgentHour(10) });

        samples.Should().HaveCount(2);
        samples.Should().OnlyContain(s => s.Utilization == 44 && s.TxRetryPct == 0);
    }

    [Fact]
    public void The_agent_reports_the_channel_actually_live_across_a_mid_window_change()
    {
        // Console events say the radio moved 1 -> 6 at 09:30; the agent measured each hour itself.
        var events = new List<ChannelChangeEvent>
        {
            new()
            {
                Timestamp = new DateTimeOffset(2026, 8, 24, 9, 30, 0, TimeSpan.Zero),
                ApMac = Ap, Band = RadioBand.Band2_4GHz, PreviousChannel = 1, NewChannel = 6,
            },
        };

        var samples = Build(
            new List<SiteWiFiMetrics> { Metric(8), Metric(9), Metric(10) },
            new[] { AgentHour(8, channel: 1), AgentHour(10, channel: 6) },
            events);

        samples.Should().HaveCount(3);
        samples.Should().ContainSingle(s => s.TimestampUtc.Hour == 8).Which.Channel.Should().Be(1);
        samples.Should().ContainSingle(s => s.TimestampUtc.Hour == 9).Which.Channel
            .Should().Be(1, "the console sample at 09:00 predates the 09:30 change");
        samples.Should().ContainSingle(s => s.TimestampUtc.Hour == 10).Which.Channel.Should().Be(6);
    }

    [Fact]
    public void An_agent_won_hour_adopts_the_console_tx_retry_when_the_attribution_agrees()
    {
        var samples = Build(
            new List<SiteWiFiMetrics> { Metric(7, txRetry: 3.5) },
            new[] { AgentHour(7, channel: 6) });

        samples.Should().ContainSingle().Which.TxRetryPct.Should().Be(3.5,
            "the agent has no radio-level retry counter, so the console's stays authoritative");
    }

    [Fact]
    public void Console_tx_retry_is_not_adopted_when_the_sources_disagree_on_the_channel()
    {
        // Console attribution says channel 6 for hour 7; the agent measured channel 11 live.
        var samples = Build(
            new List<SiteWiFiMetrics> { Metric(7, txRetry: 3.5) },
            new[] { AgentHour(7, channel: 11) });

        var s = samples.Should().ContainSingle().Subject;
        s.Channel.Should().Be(11, "the agent's measured config is ground truth");
        s.TxRetryPct.Should().Be(0, "a retry figure measured on a disputed hour must not follow the agent's channel");
    }

    [Fact]
    public void Agent_hours_outside_the_window_or_for_another_radio_are_ignored()
    {
        var samples = Build(
            new List<SiteWiFiMetrics>(),
            new[]
            {
                AgentHour(5),
                AgentHour(12),
                AgentHour(7, apMac: "aa:bb:cc:dd:ee:02"),
                AgentHour(7, band: "na"),
            });

        samples.Should().BeEmpty(
            "the watermark window and (AP, band) identity gate agent hours exactly like console rows");
    }

    [Fact]
    public void Samples_and_agent_hours_respect_the_window_bounds()
    {
        var samples = Build(
            new List<SiteWiFiMetrics> { Metric(5), Metric(6), Metric(11), Metric(12) },
            new[] { AgentHour(11) });

        samples.Should().HaveCount(2);
        samples.Should().ContainSingle(s => s.TimestampUtc.Hour == 6 && s.Utilization == 30);
        samples.Should().ContainSingle(s => s.TimestampUtc.Hour == 11 && s.Utilization == 44);
    }
}
