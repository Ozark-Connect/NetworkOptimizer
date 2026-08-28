using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>Whether a device passed its post-upgrade checks, and why not when it did not.</summary>
/// <param name="Passed">True when nothing objected.</param>
/// <param name="Reason">What failed, for the step's Error field and the SKU-abort alert.</param>
public sealed record LitmusVerdict(bool Passed, string? Reason = null, bool Silence = false)
{
    /// <summary>Nothing objected.</summary>
    public static LitmusVerdict Pass() => new(true);

    /// <summary>Something objected.</summary>
    public static LitmusVerdict Fail(string reason) => new(false, reason);

    /// <summary>
    /// The device said nothing at all. A failure like any other where we were watching, but the
    /// caller may know we were not - a device cannot be condemned for silence nobody heard.
    /// </summary>
    public static LitmusVerdict Silent(string reason) => new(false, reason, Silence: true);
}

/// <summary>
/// The post-upgrade checks: the short canary litmus that decides whether the rest of an SKU may
/// roll, and the before/after resource windows behind the regression and improvement alerts.
/// </summary>
public interface IRolloutLitmusService
{
    /// <summary>
    /// Mean CPU and memory over a window, from the site's device health history.
    /// </summary>
    /// <param name="deviceMac">Device MAC in any format.</param>
    /// <param name="from">Window start (UTC).</param>
    /// <param name="to">Window end (UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RolloutResourceStats> CaptureStatsAsync(string deviceMac, DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>
    /// The short canary litmus, run after the cool-down: is the device answering at all, is its
    /// resource use anywhere near where it was, and - only when it is itself a monitored latency
    /// target - is it losing packets.
    /// </summary>
    /// <param name="deviceMac">Device MAC in any format.</param>
    /// <param name="preStats">Pre-upgrade window captured when the command went out.</param>
    /// <param name="from">Start of the observation window (UTC), normally the cool-down end.</param>
    /// <param name="to">End of the observation window (UTC), normally now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<LitmusVerdict> RunShortLitmusAsync(
        string deviceMac,
        RolloutResourceStats? preStats,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRolloutLitmusService" />
public class LitmusService : IRolloutLitmusService
{
    private readonly MonitoringInfluxClient _influx;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly ILogger<LitmusService> _logger;
    private readonly string _siteSlug;
    private readonly bool _isDefault;

    /// <param name="influxRegistry">Per-site InfluxDB clients.</param>
    /// <param name="dbFactory">Main database context factory.</param>
    /// <param name="siteDbFactory">Per-site database context factory.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site this instance evaluates for.</param>
    public LitmusService(
        MonitoringInfluxRegistry influxRegistry,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        SiteDbContextFactory siteDbFactory,
        ILogger<LitmusService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _isDefault = _siteSlug == SiteManagementService.DefaultSiteSlug;
        _influx = influxRegistry.GetFor(_siteSlug);
        _dbFactory = dbFactory;
        _siteDbFactory = siteDbFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RolloutResourceStats> CaptureStatsAsync(
        string deviceMac, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceMac) || to <= from)
            return new RolloutResourceStats();

        from = DateTimeUtilities.AsUtc(from);
        to = DateTimeUtilities.AsUtc(to);

        try
        {
            // QueryDeviceHealthAsync normalizes the MAC itself, so either spelling is fine here.
            var points = await _influx.QueryDeviceHealthAsync(deviceMac, from, to, ct: cancellationToken);

            // Loss is read whether or not health reported: the pre-upgrade window is the only
            // baseline the canary's loss check ever gets, and a device can be probed without
            // reporting health of its own.
            var loss = await MeasureTargetLossAsync(deviceMac, from, to, cancellationToken);
            if (points.Count == 0)
                return new RolloutResourceStats { LossPercent = loss };

            var cpu = points.Where(p => p.CpuPercent.HasValue).Select(p => p.CpuPercent!.Value).ToList();
            var memory = points.Where(p => p.MemoryUsedPercent.HasValue).Select(p => p.MemoryUsedPercent!.Value).ToList();

            return new RolloutResourceStats
            {
                CpuPercent = cpu.Count > 0 ? cpu.Average() : null,
                MemoryUsedPercent = memory.Count > 0 ? memory.Average() : null,
                SampleCount = points.Count,
                LossPercent = loss,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading device health for {Mac} on site {Site} failed", deviceMac, _siteSlug);
            return new RolloutResourceStats();
        }
    }

    /// <inheritdoc />
    public async Task<LitmusVerdict> RunShortLitmusAsync(
        string deviceMac,
        RolloutResourceStats? preStats,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
    {
        var post = await CaptureStatsAsync(deviceMac, from, to, cancellationToken);

        // Probes are evidence in their own right, so this is judged before health silence - a
        // device that answers pings badly and reports nothing at all is still a failure.
        if (LitmusThresholds.IsAppreciableLoss(preStats?.LossPercent, post.LossPercent))
        {
            var before = preStats?.LossPercent is > 0 ? $" (was {preStats.LossPercent:0.0}%)" : "";
            return LitmusVerdict.Fail(
                $"The device is a monitored latency target and is losing {post.LossPercent:0.0}% of probes{before}.");
        }

        // Silence only counts against a device that was being heard before. A site that collects no
        // device health at all would otherwise fail every canary it ever ran.
        if (!post.HasSamples)
        {
            return preStats is { HasSamples: true }
                ? LitmusVerdict.Silent("The device stopped reporting health after the upgrade.")
                : LitmusVerdict.Pass();
        }

        var comparison = LitmusThresholds.Compare(preStats, post);
        if (comparison.Verdict == ResourceComparisonVerdict.Regression)
            return LitmusVerdict.Fail($"Resource use jumped after the upgrade. {comparison.Detail}");

        return LitmusVerdict.Pass();
    }

    /// <summary>
    /// Probes are sparse and quantized - three pings a sample makes every sample 0, 33, 67 or 100 -
    /// so the window needs enough of them to say anything, and one bad sample must not carry it.
    /// Below this the loss check returns no reading and the other checks decide, which is the same
    /// path a device that is not probed at all already takes.
    /// </summary>
    public const int MinLossSamples = 10;

    /// <summary>
    /// Median loss over the window when this device is itself a monitored latency target, else null.
    /// Loss is only evidence for devices we probe; every other device has nothing to read.
    /// <para>
    /// Median, not mean: a single blip inside a short window drags a mean over the fail threshold
    /// while every sample either side of it is clean. Two dropped pings in a two-minute window was
    /// enough to fail a healthy access point.
    /// </para>
    /// </summary>
    private async Task<double?> MeasureTargetLossAsync(
        string deviceMac, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        try
        {
            var targetIds = await ResolveLatencyTargetIdsAsync(deviceMac, cancellationToken);
            if (targetIds.Count == 0) return null;

            double worst = 0;
            var sawAny = false;
            foreach (var targetId in targetIds)
            {
                var points = await _influx.QueryLatencyAsync(targetId, from, to, ct: cancellationToken);
                var losses = points.Where(p => p.LossPercent.HasValue).Select(p => p.LossPercent!.Value).ToList();
                if (losses.Count < MinLossSamples)
                {
                    if (losses.Count > 0)
                    {
                        _logger.LogDebug(
                            "Not judging probe loss for {Mac} on site {Site}: {Count} sample(s) over the window, {Min} needed",
                            deviceMac, _siteSlug, losses.Count, MinLossSamples);
                    }
                    continue;
                }
                sawAny = true;
                worst = Math.Max(worst, Median(losses));
            }

            return sawAny ? worst : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading probe loss for {Mac} on site {Site} failed", deviceMac, _siteSlug);
            return null;
        }
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    private async Task<List<string>> ResolveLatencyTargetIdsAsync(string deviceMac, CancellationToken cancellationToken)
    {
        var normalized = MacNormalizer.Normalize(deviceMac);
        await using var db = await CreateSiteDbAsync(cancellationToken);
        var candidates = await db.MonitoringTargets
            .AsNoTracking()
            .Where(t => t.Enabled && t.DeviceMac != null && t.RetiredAt == null)
            .Select(t => new { t.TargetId, t.DeviceMac })
            .ToListAsync(cancellationToken);

        return candidates
            .Where(t => MacNormalizer.Normalize(t.DeviceMac!) == normalized)
            .Select(t => t.TargetId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();
    }

    /// <summary>Context for the database holding this instance's site data.</summary>
    private async Task<NetworkOptimizerDbContext> CreateSiteDbAsync(CancellationToken cancellationToken)
    {
        if (!_isDefault)
            return _siteDbFactory.CreateForSite(_siteSlug, isDefault: false);
        return await _dbFactory.CreateDbContextAsync(cancellationToken);
    }
}
