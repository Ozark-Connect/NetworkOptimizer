using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.ChangeControl;
using NetworkOptimizer.Web.Services.ChangeControl.SiteHealth;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class SiteHealthTests
{
    private static readonly DateTime T = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AccessProfile Fiber = IspHealthProfiles.GetProfile(AccessTechnology.Gpon)!;

    private static SiteHealthHour Score(HealthHourInputs x) => SiteHealthScorer.Score(x, Fiber, T);

    [Fact]
    public void CleanHour_Is100()
    {
        var h = Score(new HealthHourInputs { Hour = T, LossPct = 0, IdleRttMs = 2, WifiSamples = 100 });
        h.Health.Should().Be(100);
        h.Worst.Should().BeNull();
    }

    [Fact]
    public void NoData_IsUnknown_NotHundred()
    {
        Score(new HealthHourInputs { Hour = T }).Health.Should().BeNull();
    }

    [Fact]
    public void Outage_IsShareOfTheHour_AndIsTheIsps()
    {
        var h = Score(new HealthHourInputs { Hour = T, LossPct = 0, IdleRttMs = 2, OutageMinutes = 15 });
        h.Health.Should().Be(75);
        h.InternetIsp.Should().BeApproximately(0.25, 0.0001);
        h.Worst.Should().Contain("WAN outage").And.Contain("ISP");
    }

    [Fact]
    public void PartialOutage_IsLabeledByWhatFailed_NotAsTheWan()
    {
        var h = Score(new HealthHourInputs
        {
            Hour = T, LossPct = 0, IdleRttMs = 2, OutageMinutes = 0.56,
            OutageText = "partial WAN outage, 2 of 27 targets, 7 min",
        });
        h.Worst.Should().Be("Internet: partial WAN outage, 2 of 27 targets, 7 min (ISP)");
    }

    [Fact]
    public void LocalCause_MovesTheImpairmentOffTheIsp()
    {
        var h = Score(new HealthHourInputs { Hour = T, LossPct = 0, IdleRttMs = 2, OutageMinutes = 30, LocalCause = "gateway rebooted" });
        h.Health.Should().Be(50);
        h.InternetIsp.Should().Be(0);
        SiteHealthScorer.LocalHealth(h).Should().Be(50);
    }

    [Fact]
    public void LocalHealth_RemovesTheIspPart()
    {
        var h = Score(new HealthHourInputs { Hour = T, LossPct = 0, IdleRttMs = 2, OutageMinutes = 30 });
        h.Health.Should().Be(50);
        SiteHealthScorer.LocalHealth(h).Should().BeApproximately(100, 0.01);
    }

    [Fact]
    public void Wifi_CountsUnder60Fully_60To80Half_WeightedByWirelessShare()
    {
        var h = Score(new HealthHourInputs
        {
            Hour = T, WifiSamples = 100, WifiBelow80 = 20, WifiBelow60 = 10, WirelessClients = 5, WiredClients = 5,
            WorstRadio = "AP1 5 GHz", WorstRadioShare = 0.2,
        });
        // 0.15 raw, damped by 5 / (5 + 4).
        h.Wifi.Should().BeApproximately(0.15 * 5 / 9, 0.0001);
        h.WirelessShare.Should().Be(0.5);
        h.Health.Should().BeApproximately(100 * (1 - 0.15 * 5 / 9 * 0.5), 0.05);
        h.Worst.Should().Be("Wi-Fi: AP1 5 GHz, 20% of client satisfaction under 80");
    }

    /// <summary>One client's samples are 1/n of the hour's; every sample of a bad client is under 60.</summary>
    private static SiteHealthHour WifiHour(int badClients, int clients) => Score(new HealthHourInputs
    {
        Hour = T, WifiSamples = clients * 12, WifiBelow80 = badClients * 12, WifiBelow60 = badClients * 12,
        WirelessClients = clients,
    });

    [Fact]
    public void Wifi_OneBadOfFour_IsDampedToAnEighth()
    {
        // Undamped this is 25% of the site; at n = k it counts half.
        WifiHour(1, 4).Wifi.Should().BeApproximately(0.125, 0.0001);
    }

    [Fact]
    public void Wifi_OneBadOfForty_IsBarelyDamped()
    {
        WifiHour(1, 40).Wifi.Should().BeApproximately(0.025 * 40 / 44, 0.0001);
    }

    [Fact]
    public void Wifi_TenBadOfForty_KeepsMostOfItsWeight()
    {
        var h = WifiHour(10, 40);
        h.Wifi.Should().BeApproximately(0.25 * 40 / 44, 0.0001);
        h.Wifi.Should().BeGreaterThan(WifiHour(1, 4).Wifi!.Value, "the same 25% across ten times the clients is a real problem");
    }

    [Fact]
    public void Latency_IsGradedOnTheProfileFloor_NotJustTheBaseline()
    {
        // A 1.5 ms baseline would flag 3 ms as 2x normal; GPON's normal-high floor (3 ms) does not.
        Score(new HealthHourInputs { Hour = T, LossPct = 0, IdleRttMs = 3, BaselineRttMs = 1.5 }).Health.Should().Be(100);
        Score(new HealthHourInputs { Hour = T, LossPct = 0, IdleRttMs = 12, BaselineRttMs = 1.5 }).Health.Should().BeLessThan(80);
    }

    [Fact]
    public void Bufferbloat_IsLocal_AndScalesWithLoadedShare()
    {
        var h = Score(new HealthHourInputs { Hour = T, LossPct = 0, IdleRttMs = 2, LoadedRttMs = 2 + 4 * Fiber.LoadedDeltaAcceptableMs, LoadedShare = 0.5 });
        h.Health.Should().BeApproximately(50, 0.05);
        h.InternetIsp.Should().Be(0);
        h.Worst.Should().Contain("bufferbloat");
    }

    [Fact]
    public void Topology_GatewayOutageTakesEveryoneBehindIt()
    {
        var topology = new SiteHealthJob.Topology(
        [
            new DiscoveredDevice { Mac = "aa:00:00:00:00:01", Name = "Gateway", Type = DeviceType.Gateway },
            new DiscoveredDevice { Mac = "aa:00:00:00:00:02", Name = "Switch", Type = DeviceType.Switch, UplinkMac = "aa:00:00:00:00:01" },
            new DiscoveredDevice { Mac = "aa:00:00:00:00:03", Name = "AP", Type = DeviceType.AccessPoint, UplinkMac = "aa:00:00:00:00:02" },
        ]);
        var clients = new Dictionary<string, int> { ["aa:00:00:00:00:02"] = 6, ["aa:00:00:00:00:03"] = 4 };

        topology.Availability(new() { ["aa:00:00:00:00:01"] = 0.5 }, new() { ["aa:00:00:00:00:01"] = "[Gateway] Gateway" }, clients)
            .Impairment.Should().Be(0.5);
        var ap = topology.Availability(new() { ["aa:00:00:00:00:03"] = 1 }, new() { ["aa:00:00:00:00:03"] = "[AP] AP" }, clients);
        ap.Impairment.Should().BeApproximately(0.4, 0.0001);
        ap.Text.Should().Be("[AP] AP offline 60 min");
    }

    [Fact]
    public void Baseline_UsesTheSameHourOfWeek_WhenThereAreThree()
    {
        var history = Enumerable.Range(1, 14).Select(d => new SiteHealthHour { HourStart = T.AddDays(-d), AccessRttMs = d % 7 == 0 ? 20 : 2 }).ToList();
        SiteHealthJob.Baseline(history, T).Should().Be(2);
        var weekly = new[] { 7, 14 }.Select(d => new SiteHealthHour { HourStart = T.AddDays(-d), AccessRttMs = 9 })
            .Concat(new[] { new SiteHealthHour { HourStart = T.AddHours(-1), AccessRttMs = 3 } }).ToList();
        SiteHealthJob.Baseline(weekly, T).Should().Be(9, "fewer than 3 same-hour values falls back to the 14-day median");
    }

    [Fact]
    public void HealthAround_MeansThreeHoursEachSide_LocalOnly()
    {
        var hours = Enumerable.Range(-4, 9).Select(i => new SiteHealthHour
        {
            HourStart = T.AddHours(i), Health = i < 0 ? 98 : 80, InternetIsp = 0,
        }).ToList();
        var (before, after, _, _) = SiteHealthJob.HealthAround(hours, T.AddMinutes(10));
        before.Should().Be(98);
        after.Should().Be(80);
    }

    [Fact]
    public void Series_BucketsLongRanges_AndNamesTheTopIssue()
    {
        var hours = Enumerable.Range(0, 24 * 30).Select(i => Score(new HealthHourInputs
        {
            Hour = T.AddHours(i), LossPct = 0, IdleRttMs = 2, WifiSamples = 100, WifiBelow80 = i % 2 == 0 ? 20 : 0, WorstRadio = "AP1 5 GHz", WorstRadioShare = 0.2,
        })).ToList();
        var series = SiteHealthSeriesBuilder.Build(hours, T, T.AddDays(30));
        series.Points.Count.Should().BeLessThanOrEqualTo(200);
        series.TopArea.Should().Be("Wi-Fi");
        series.TopIssue.Should().Be("Wi-Fi: AP1 5 GHz (avg 5 health points)");
        series.TopDetail.Should().Be("20% of client satisfaction under 80");
        series.TopDetailAt.Should().Be(hours.Last(h => h.DetailsJson != null).HourStart);
        series.TopShort.Should().Be("Wi-Fi: AP1 5 GHz");
        series.Describe(TimeZoneInfo.Utc, DisplayFormat.Fallback).Should()
            .StartWith("Wi-Fi: AP1 5 GHz (avg 5 health points). Latest, ").And.EndWith(": 20% of client satisfaction under 80");
    }

    [Fact]
    public void Series_KeepsANonWiFiIssueWhole()
    {
        var hours = Enumerable.Range(0, 24).Select(i => Hour(i,
            i == 8 ? ("Internet", 0.047, "WAN outage, 3 min (ISP)") : null)).ToList();

        var series = SiteHealthSeriesBuilder.Build(hours, T, T.AddDays(1));

        series.TopIssue.Should().Be("Internet: WAN outage, 3 min (ISP) (avg < 1 health point)");
        series.TopDetail.Should().BeNull();
        series.Describe(TimeZoneInfo.Utc, DisplayFormat.Fallback).Should().Be(series.TopIssue);
    }

    [Fact]
    public void TopIssue_AShortSharpOutageShowsThoughItsDailyAverageIsSmall()
    {
        var hours = Enumerable.Range(0, 24).Select(i => Hour(i,
            i == 8 ? ("Internet", 0.047, "partial WAN outage, 4 of 27 targets, 19 min (ISP)") : null,
            ("Wi-Fi", 0.0012, "AP1 5 GHz, 1% of client satisfaction under 80"))).ToList();

        var (area, text, _, _) = SiteHealthSeriesBuilder.TopIssue(hours);

        area.Should().Be("Internet");
        text.Should().Be("partial WAN outage, 4 of 27 targets, 19 min (ISP)");
    }

    [Fact]
    public void TopIssue_SteadyNoiseUnderBothBarsStaysHidden()
    {
        var hours = Enumerable.Range(0, 24).Select(i => Hour(i, ("Wi-Fi", 0.004, "AP1 5 GHz, 1% of client satisfaction under 80"))).ToList();

        SiteHealthSeriesBuilder.TopIssue(hours).Area.Should().BeNull();
    }

    [Fact]
    public void TopIssue_ASteadyIssueThatNeverPeaksStillShows()
    {
        var hours = Enumerable.Range(0, 24).Select(i => Hour(i, ("Wi-Fi", 0.015, "AP1 5 GHz, 15% of client satisfaction under 80"))).ToList();

        SiteHealthSeriesBuilder.TopIssue(hours).Area.Should().Be("Wi-Fi");
    }

    private static SiteHealthHour Hour(int i, params (string Area, double Impairment, string Text)?[] contributors) => new()
    {
        HourStart = T.AddHours(i),
        Health = 100,
        DetailsJson = System.Text.Json.JsonSerializer.Serialize(contributors.OfType<(string Area, double Impairment, string Text)>()
            .Select(c => new HealthContributor(c.Area, c.Impairment, c.Text)).ToList()),
    };

    [Fact]
    public void HealthDeltas_ReportsTheDropAndTheAreaThatMoved()
    {
        var hours = Enumerable.Range(-4, 9).Select(i => Score(new HealthHourInputs
        {
            Hour = T.AddHours(i), LossPct = 0, IdleRttMs = 2, WifiSamples = 100, WifiBelow80 = i > 0 ? 40 : 0, WifiBelow60 = i > 0 ? 40 : 0,
        })).ToList();
        var rows = ChangeControlService.HealthDeltas(hours, T.AddMinutes(5));
        rows[0].Should().Be(new MetricDelta("Site Health (3 h)", "100", "60", true));
        rows.Should().Contain(r => r.Label == "Wi-Fi, points lost" && r.After == "40");
    }

    private static List<LossWindow> QuietHour(LossWindow burst) =>
        [burst, .. Enumerable.Range(0, 11).Select(_ => new LossWindow(0, 0, false))];

    [Fact]
    public void Loss_ShortBurstCostsItsShareOfTheHour()
    {
        var h = Score(new HealthHourInputs { Hour = T, IdleRttMs = 2, LossWindows = QuietHour(new LossWindow(2.75, 0, false)) });

        h.Health.Should().Be(91.7);
        h.Worst.Should().Be("Internet: loss 2.75% for 5 min (ISP)");
    }

    [Fact]
    public void Loss_AlongsideLanLossIsNotTheIsps()
    {
        var h = Score(new HealthHourInputs { Hour = T, IdleRttMs = 2, LossWindows = QuietHour(new LossWindow(8, 0, true)) });

        h.Health.Should().Be(100);
        h.InternetIsp.Should().Be(0);
    }

    [Fact]
    public void Loss_WhileTheGatewayIsBusyIsTheGateways()
    {
        var h = Score(new HealthHourInputs { Hour = T, IdleRttMs = 2, LossWindows = QuietHour(new LossWindow(2.75, 0, false, 95, 940)) });

        h.Health.Should().Be(91.7);
        h.InternetIsp.Should().Be(0);
        h.Worst.Should().Be("Internet: loss 2.75% for 5 min at 940 Mbps (gateway CPU 95%)");
    }

    [Fact]
    public void Loss_AlongsideLanLossStaysTheLansEvenWithABusyGateway()
    {
        var h = Score(new HealthHourInputs { Hour = T, IdleRttMs = 2, LossWindows = QuietHour(new LossWindow(8, 0, true, 95, 100)) });

        h.Health.Should().Be(100);
    }

    [Fact]
    public void Loss_WithTheGatewayUnderTheBusyLineIsTheIsps()
    {
        var h = Score(new HealthHourInputs { Hour = T, IdleRttMs = 2, LossWindows = QuietHour(new LossWindow(2.75, 0, false, 60, 940)) });

        h.InternetIsp.Should().BeGreaterThan(0);
        h.Worst.Should().Be("Internet: loss 2.75% for 5 min (ISP)");
    }

    [Fact]
    public void Loss_UnderLoadIsJudgedAgainstTheLoadedBand()
    {
        var idle = Score(new HealthHourInputs { Hour = T, IdleRttMs = 2, LossWindows = QuietHour(new LossWindow(Fiber.LoadedLossDownLowPct, 0, false)) });
        var loaded = Score(new HealthHourInputs { Hour = T, IdleRttMs = 2, LossWindows = QuietHour(new LossWindow(Fiber.LoadedLossDownLowPct, 1, false)) });

        idle.Health.Should().BeLessThan(100);
        loaded.Health.Should().Be(100);
    }

    [Fact]
    public void LossCeiling_RisesQuadraticallyFromIdleToTheLoadedBand()
    {
        SiteHealthScorer.LossCeiling(Fiber, 0).Should().Be(Math.Max(SiteHealthScorer.NoticeableLossPct, Fiber.IdleLossAcceptablePct));
        SiteHealthScorer.LossCeiling(Fiber, 1).Should().BeApproximately(Fiber.LoadedLossDownLowPct, 1e-9);
        SiteHealthScorer.LossCeiling(Fiber, 0.5).Should().BeApproximately(
            Math.Max(SiteHealthScorer.NoticeableLossPct, Fiber.IdleLossAcceptablePct + 0.25 * (Fiber.LoadedLossDownLowPct - Fiber.IdleLossAcceptablePct)), 1e-9);
    }

    [Fact]
    public void FabricLoss_ShortBurstCostsItsShareOfTheHour()
    {
        var h = Score(new HealthHourInputs { Hour = T, IdleRttMs = 2, LossPct = 0, FabricWindows = [5, .. Enumerable.Repeat(0.0, 11)] });

        h.Health.Should().Be(91.7);
        h.Worst.Should().Be("LAN: LAN loss 5% for 5 min");
    }

    [Fact]
    public void WindowLoad_AveragesUtilizationAgainstSaturation()
    {
        var start = T;
        var rates = Enumerable.Range(0, 10).Select(i => new ThroughputSample(start.AddSeconds(30 * i), i < 5 ? 1000e6 : 200e6, 0)).ToList();

        var w = SiteHealthJob.WindowLoad(rates, [], TimeSpan.FromSeconds(7), TimeSpan.FromMinutes(5), 1000, 1000, 0.75)[start];

        w.Total.Should().Be(10);
        w.Saturation.Should().BeApproximately(0.6 / 0.75, 1e-9);
    }

    [Theory]
    [InlineData("Internet", "/monitoring?tab=isp-health")]
    [InlineData("Wi-Fi", "/wifi-optimizer")]
    [InlineData("Availability", "/monitoring?tab=devices")]
    [InlineData("LAN", "/monitoring?tab=performance&wan=lan")]
    [InlineData(null, null)]
    public void IssueHref_OpensThePageThatShowsTheArea(string? area, string? href)
    {
        SiteHealthSeriesBuilder.IssueHref(area).Should().Be(href);
        (SiteHealthSeriesBuilder.IssueHrefLabel(area) == null).Should().Be(href == null);
    }

    private static MonitoringInfluxClient.TargetLatencyMean Row(int minute, string target, string type, double? rtt, double? loss = 0) =>
        new(T.AddMinutes(minute), target, type, rtt, loss);

    [Fact]
    public void AccessFloorRows_TakeLowestAccessRttPerWindow_IgnoringOtherTargetTypes()
    {
        var pool = new List<MonitoringInfluxClient.TargetLatencyMean>
        {
            Row(0, "a", "accessisp", 1.8), Row(5, "a", "accessisp", 9.0),
            Row(0, "b", "accessisp", 2.0), Row(5, "b", "accessisp", 2.1),
            Row(0, "t", "transit", 0.5), Row(5, "t", "transit", 0.5),
        };

        var floor = SiteHealthJob.AccessFloorRows(pool);

        floor.Select(r => r.RttAvgMs).Should().Equal(1.8, 2.1);
    }

    [Fact]
    public void AccessFloorRows_OneAccessHop_IsThatHop()
    {
        var pool = new List<MonitoringInfluxClient.TargetLatencyMean> { Row(0, "a", "accessisp", 1.8), Row(5, "a", "accessisp", 9.0) };

        SiteHealthJob.AccessFloorRows(pool).Select(r => r.RttAvgMs).Should().Equal(1.8, 9.0);
    }

    [Fact]
    public void AccessFloorRows_NoAccessHop_IsEmpty()
    {
        SiteHealthJob.AccessFloorRows([Row(0, "t", "transit", 0.5)]).Should().BeEmpty();
    }
}
