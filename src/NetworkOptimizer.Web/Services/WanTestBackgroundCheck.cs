using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.SqmLearning;

namespace NetworkOptimizer.Web.Services;

/// <summary>When each phase of a UWN WAN test ran, in UTC, from the binary's JSON.</summary>
internal sealed record WanTestPhases(
    (DateTime From, DateTime To)? Idle,
    (DateTime From, DateTime To)? Download,
    (DateTime From, DateTime To)? Upload)
{
    /// <summary>The phases the binary reported, or null for a binary that predates them.</summary>
    public static WanTestPhases? From(WanSpeedTestResult json)
    {
        static (DateTime, DateTime)? Window(DateTime? from, DateTime? to) =>
            from is DateTime f && to is DateTime t && t > f ? (f.ToUniversalTime(), t.ToUniversalTime()) : null;

        var phases = new WanTestPhases(
            Window(json.IdleLoss?.StartedAt, json.IdleLoss?.EndedAt),
            Window(json.Download?.StartedAt, json.Download?.EndedAt),
            Window(json.Upload?.StartedAt, json.Upload?.EndedAt));
        return phases.All.Count == 0 ? null : phases;
    }

    public List<(DateTime From, DateTime To)> All =>
        new[] { Idle, Download, Upload }.Where(w => w.HasValue).Select(w => w!.Value).ToList();
}

/// <summary>
/// Checks a UWN WAN test for other traffic on the WAN while it ran, from monitoring's counters on the
/// WAN's counter interface. A test below <see cref="ExpectedPlanFraction"/> of plan in a direction
/// whose background covers at least <see cref="ShortfallExplainedFraction"/> of the gap measured the
/// competition, not the line, and is marked invalid. Without monitoring for that WAN nothing is
/// marked: an unverified shortfall stays an ordinary result.
/// </summary>
internal static class WanTestBackgroundCheck
{
    public const double ExpectedPlanFraction = 0.85;
    public const double ShortfallExplainedFraction = 0.5;

    /// <summary>Resolution of the counter series read back.</summary>
    private static readonly TimeSpan PointSpan = TimeSpan.FromSeconds(5);

    /// <summary>Wait after the last phase for its counter samples to reach InfluxDB.</summary>
    private static readonly TimeSpan CounterSettle = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs the check in the background once the counters have landed, updates the stored result,
    /// then calls <paramref name="onUpdated"/> so open pages reload it. Bonded multi-WAN results
    /// ("WAN+WAN2") are skipped: their phases ran on several counter interfaces at once.
    /// </summary>
    public static void Schedule(
        IServiceScopeFactory scopeFactory,
        string siteSlug,
        int resultId,
        string? wanNetworkGroup,
        WanTestPhases? phases,
        Func<Task<NetworkOptimizerDbContext>> openSiteDb,
        Action<int>? onUpdated,
        ILogger logger)
    {
        if (phases == null || string.IsNullOrEmpty(wanNetworkGroup) || wanNetworkGroup.Contains('+'))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var windows = phases.All;
                var wait = windows.Max(w => w.To) + CounterSettle - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait);

                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteSlug);
                Identity.SystemScope.Enter(scope.ServiceProvider, "speedtest:wan-background");

                var points = await scope.ServiceProvider.GetRequiredService<WanIdleGate>().ReadWindowAsync(
                    wanNetworkGroup, windows.Min(w => w.From) - PointSpan, windows.Max(w => w.To) + PointSpan * 2,
                    CancellationToken.None);
                if (points == null || points.Count == 0)
                    return;

                await using var db = await openSiteDb();
                var result = await db.Iperf3Results.FindAsync(resultId);
                if (result == null)
                    return;

                var (planDown, planUp) = await PlanSpeedsAsync(scope.ServiceProvider, db, wanNetworkGroup);
                var (bgDown, bgUp) = Measure(points, phases);

                result.BackgroundDownloadMbps = bgDown;
                result.BackgroundUploadMbps = bgUp;
                result.ExpectedDownloadMbps = planDown;
                result.ExpectedUploadMbps = planUp;
                result.BackgroundLoadInvalid = IsInvalid(result.DownloadMbps, planDown, bgDown)
                    || IsInvalid(result.UploadMbps, planUp, bgUp);
                await db.SaveChangesAsync();

                logger.LogInformation(
                    "WAN test {Id} background on {Group}: {BgDown:F0} down / {BgUp:F0} up Mbps vs plan {PlanDown} / {PlanUp} -> {Verdict}",
                    resultId, wanNetworkGroup, bgDown, bgUp, planDown, planUp,
                    result.BackgroundLoadInvalid ? "invalid" : "valid");
                onUpdated?.Invoke(resultId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background load check failed for WAN test {Id}", resultId);
            }
        });
    }

    /// <summary>
    /// Background per direction: the WAN counter rate during the idle phase, when the test sends
    /// next to nothing. Null when no counter point falls wholly inside it.
    /// <para>
    /// Never read background from a loaded phase as counter minus test rate. The counters are
    /// bursty at 5 s resolution and an 8 s phase holds one or two points: clean tests read up to
    /// 244 Mbps of background that was not there, and real background read low.
    /// </para>
    /// </summary>
    internal static (double? Down, double? Up) Measure(
        IReadOnlyList<MonitoringInfluxClient.WanRatePoint> points, WanTestPhases phases)
    {
        if (phases.Idle is not { } idle)
            return (null, null);

        // A point is stamped at the end of its 5 s window. Only points wholly inside the idle
        // phase count: the download starts the moment it ends, and a straddling point reads the
        // test's own ramp.
        var inside = points.Where(p => p.Time >= idle.From + PointSpan && p.Time <= idle.To).ToList();

        static double? MeanMbps(List<MonitoringInfluxClient.WanRatePoint> pts, Func<MonitoringInfluxClient.WanRatePoint, double?> bps)
        {
            var values = pts.Select(bps).Where(v => v.HasValue).Select(v => v!.Value / 1_000_000.0).ToList();
            return values.Count > 0 ? values.Average() : null;
        }

        return (MeanMbps(inside, p => p.DownloadBps), MeanMbps(inside, p => p.UploadBps));
    }

    /// <summary>Below the expected share of plan, with background covering enough of the gap to explain it.</summary>
    internal static bool IsInvalid(double testMbps, double? planMbps, double? backgroundMbps) =>
        planMbps is > 0 && backgroundMbps is double background
        && testMbps < planMbps.Value * ExpectedPlanFraction
        && background >= (planMbps.Value - testMbps) * ShortfallExplainedFraction;

    /// <summary>Why a result marked <see cref="Iperf3Result.BackgroundLoadInvalid"/> is not a valid test; null otherwise.</summary>
    internal static string? InvalidMessage(Iperf3Result result)
    {
        if (!result.BackgroundLoadInvalid)
            return null;

        var down = IsInvalid(result.DownloadMbps, result.ExpectedDownloadMbps, result.BackgroundDownloadMbps);
        var up = IsInvalid(result.UploadMbps, result.ExpectedUploadMbps, result.BackgroundUploadMbps);
        var traffic = (down, up) switch
        {
            (true, true) => $"{result.BackgroundDownloadMbps:F0} Mbps of other download and {result.BackgroundUploadMbps:F0} Mbps of other upload traffic were",
            (false, true) => $"{result.BackgroundUploadMbps:F0} Mbps of other upload traffic was",
            _ => $"{result.BackgroundDownloadMbps:F0} Mbps of other download traffic was",
        };
        var plan = (down, up) switch
        {
            (true, true) => $"{result.ExpectedDownloadMbps:F0} / {result.ExpectedUploadMbps:F0}",
            (false, true) => $"{result.ExpectedUploadMbps:F0}",
            _ => $"{result.ExpectedDownloadMbps:F0}",
        };
        return $"Not a valid test: {traffic} on this WAN during the test, enough to explain the shortfall from your {plan} Mbps plan. ISP Health ignores this result.";
    }

    /// <summary>The WAN's plan speeds from the console, else its remembered profile; never another WAN's.</summary>
    private static async Task<(double? Down, double? Up)> PlanSpeedsAsync(
        IServiceProvider services, NetworkOptimizerDbContext db, string wanNetworkGroup)
    {
        double? down = null, up = null;
        try
        {
            var networks = await services.GetRequiredService<UniFiConnectionService>().GetNetworksAsync();
            var net = networks.FirstOrDefault(n => n.IsWan
                && string.Equals(n.WanNetworkgroup, wanNetworkGroup, StringComparison.OrdinalIgnoreCase));
            if (net?.WanDownloadMbps > 0) down = net.WanDownloadMbps;
            if (net?.WanUploadMbps > 0) up = net.WanUploadMbps;
        }
        catch
        {
            // Console unreachable: the remembered profile below answers.
        }

        if (down == null || up == null)
        {
            var profile = await db.WanProfiles.AsNoTracking()
                .Where(w => w.WanNetworkgroup == wanNetworkGroup)
                .OrderByDescending(w => w.UpdatedAt)
                .FirstOrDefaultAsync();
            down ??= profile?.DownloadMbps;
            up ??= profile?.UploadMbps;
        }
        return (down, up);
    }
}
