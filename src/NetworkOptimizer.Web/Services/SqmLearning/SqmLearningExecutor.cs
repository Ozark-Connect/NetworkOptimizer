using System.Text.Json;
using NetworkOptimizer.Alerts;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Ssh;
using SqmConfig = NetworkOptimizer.Sqm.Models.SqmConfiguration;

namespace NetworkOptimizer.Web.Services.SqmLearning;

/// <summary>
/// Takes one Adaptive SQM learning sample when its schedule fires: waits for the WAN to be idle,
/// runs the standard gateway WAN speed test with a short duration and the shaper lifted, records
/// the sample, and recomputes the learned profile. Resolved from a scope pinned to the site the
/// task belongs to, so every dependency here is that site's.
/// </summary>
public class SqmLearningExecutor
{
    /// <summary>How soon a busy or contended attempt is retried, so a quiet stretch within the hour is caught.</summary>
    public static readonly TimeSpan BusyRetry = TimeSpan.FromMinutes(10);

    /// <summary>Connections per sample: half the standard test's, plenty to saturate a shared-medium WAN for 4 s.</summary>
    public const int SampleStreams = 10;

    /// <summary>A sample stays this far clear of a scheduled Adaptive SQM speed test so the two never overlap.</summary>
    public const int ScheduledProbeClearanceMinutes = 3;

    /// <summary>A direction that lands at or above this fraction of its lift measured the lift, not the line.</summary>
    public const double ProbeLimitedFraction = 0.95;

    /// <summary>How far the download lift is raised after a probe-limited sample.</summary>
    public const double DownloadLiftRaiseFactor = 1.5;

    /// <summary>
    /// How far the upload lift is raised after a probe-limited sample. Smaller steps than download:
    /// a lifted window costs the most upstream on cable and cellular, where bufferbloat lands first.
    /// </summary>
    public const double UploadLiftRaiseFactor = 1.25;

    /// <summary>
    /// The upload lift starts this far above nominal. A line that delivers its full nominal upload
    /// (most do) would otherwise sit inside the probe-limited band on every first sample.
    /// </summary>
    public const double UploadLiftHeadroom = 1.10;

    /// <summary>Sanity ceiling for a raised lift when the link speed is unknown.</summary>
    public const int MaxLiftMbps = 100_000;

    /// <summary>Failures alert on the third in a row and then once a day, not every hour.</summary>
    public const int FailureAlertThreshold = 3;
    public const int FailureAlertRepeatEvery = 24;

    private readonly ISqmLearningRepository _repo;
    private readonly ISpeedTestRepository _speedTests;
    private readonly IScheduleRepository _schedules;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly IGatewayWanSpeedTestService _gatewayTest;
    private readonly ISqmService _sqmService;
    private readonly WanIdleGate _idleGate;
    private readonly ILogger<SqmLearningExecutor> _logger;

    public SqmLearningExecutor(
        ISqmLearningRepository repo,
        ISpeedTestRepository speedTests,
        IScheduleRepository schedules,
        IGatewaySshService gatewaySsh,
        IGatewayWanSpeedTestService gatewayTest,
        ISqmService sqmService,
        WanIdleGate idleGate,
        ILogger<SqmLearningExecutor> logger)
    {
        _repo = repo;
        _speedTests = speedTests;
        _schedules = schedules;
        _gatewaySsh = gatewaySsh;
        _gatewayTest = gatewayTest;
        _sqmService = sqmService;
        _idleGate = idleGate;
        _logger = logger;
    }

    public async Task<ScheduleRunOutcome> RunAsync(int taskId, string? targetId, string? targetConfig, CancellationToken ct)
    {
        var cfg = SqmLearningTaskConfig.Parse(targetConfig);
        if (cfg == null)
            return new ScheduleRunOutcome(false, null, "This learning schedule has no configuration; start it again from Adaptive SQM.");

        var profile = await _repo.GetProfileAsync(cfg.WanNumber, ct) ?? new SqmCongestionProfile
        {
            WanNumber = cfg.WanNumber,
            Interface = targetId ?? "",
            Name = cfg.WanName ?? $"WAN{cfg.WanNumber}",
            ConnectionType = cfg.ConnectionType,
            ScheduledTaskId = taskId,
            LearningStartedAt = DateTime.UtcNow,
            LearningEndsAt = cfg.EndsAt,
            SampleDurationSeconds = cfg.DurationSeconds,
        };

        var now = DateTime.UtcNow;
        if (now >= cfg.EndsAt)
            return await CompleteAsync(taskId, profile, ct);

        // Sizing for the shaper lift: the saved WAN configuration when there is one (the user may
        // have refined the nominal speeds since), else the figures captured when learning started.
        // Without a lift a sample would measure whatever rate the shaper holds, which is exactly
        // what learning must not record.
        var saved = await _speedTests.GetSqmWanConfigAsync(cfg.WanNumber, ct);
        var wanConfig = saved is { NominalDownloadMbps: > 0 } ? saved : cfg.ToWanConfiguration(targetId);
        if (wanConfig.NominalDownloadMbps <= 0)
            return await FailAsync(profile, "No nominal speeds are known for this WAN; start learning again from the Adaptive SQM page.", ct);

        var iface = !string.IsNullOrEmpty(targetId) ? targetId : wanConfig.Interface;
        if (string.IsNullOrEmpty(iface))
            return await FailAsync(profile, "The WAN has no interface configured for Adaptive SQM.", ct);
        var ifaceCheck = InputSanitizer.ValidateInterface(iface);
        if (!ifaceCheck.isValid)
            return await FailAsync(profile, $"Invalid WAN interface: {ifaceCheck.error}", ct);

        // The gateway probe always runs: it supplies the gateway's local day and hour (the slot the
        // deployed schedule reads) and whether the SQM speed test script is busy. Its traffic
        // reading is the fallback; the monitored SNMP series decides idleness when it is available.
        var (ok, output) = await _gatewaySsh.RunCommandAsync(
            GatewayInterfaceRateProbe.BuildCommand(iface), TimeSpan.FromSeconds(30), ct);
        var gateway = ok ? GatewayInterfaceRateProbe.Parse(output) : null;
        if (gateway == null)
            return await FailAsync(profile, $"Could not read WAN traffic from the gateway: {Trim(output)}", ct);
        if (!gateway.InterfacePresent)
            return await FailAsync(profile, $"Interface {iface} was not found on the gateway.", ct);

        var wanGroup = cfg.WanGroup ?? await ResolveWanGroupAsync(iface);
        var monitored = wanGroup != null ? await _idleGate.ReadMonitoredAsync(wanGroup, ct) : null;
        var idle = monitored ?? new WanIdleGate.IdleReading(gateway.DownloadMbps, gateway.UploadMbps, "gateway");

        if (gateway.SqmSpeedtestRunning)
            return Wait("Waiting for the Adaptive SQM speed test to finish");
        if (saved != null && ScheduledProbeImminent(gateway.Hour, gateway.Minute, saved, ScheduledProbeClearanceMinutes))
            return Wait("Waiting for the scheduled Adaptive SQM speed test to pass");
        if (!idle.IsIdleFor(wanConfig.NominalDownloadMbps, wanConfig.NominalUploadMbps))
        {
            _logger.LogDebug("Learning sample deferred on {Iface}: {Down} down / {Up} up ({Source})",
                iface, FormatMbps(idle.DownloadMbps), FormatMbps(idle.UploadMbps), idle.Source);
            return Wait("Waiting for a quiet moment on the WAN");
        }

        // The lift starts from the configuration and rises after any sample that ran into it, so a
        // conservative nominal cannot cap what learning sees.
        var ceiling = LinkCeiling(wanConfig);
        var lift = RaiseLift(BuildLift(wanConfig), profile.LiftDownloadMbps, profile.LiftUploadMbps, ceiling);
        profile.LiftCeilingMbps = ceiling;
        var options = new GatewayWanTestOptions
        {
            DurationSeconds = cfg.DurationSeconds,
            Streams = SampleStreams,
            Ephemeral = true,
            ShaperLift = lift,
        };

        Iperf3Result? result;
        try
        {
            result = await _gatewayTest.RunTestAsync(iface, wanGroup, cfg.WanName ?? profile.Name,
                onProgress: null, allInterfaces: null, maxMode: false, cancellationToken: ct, options: options);
        }
        catch (Exception ex)
        {
            return await FailAsync(profile, $"Gateway speed test failed: {ex.Message}", ct);
        }

        if (result == null)
            return Wait("Waiting for another WAN speed test to finish");

        var sample = new SqmLearningSample
        {
            WanNumber = cfg.WanNumber,
            SampledAt = DateTime.UtcNow,
            LocalDayOfWeek = gateway.DayOfWeek,
            LocalHour = gateway.Hour,
            DownloadMbps = Math.Round(result.DownloadMbps, 2),
            UploadMbps = Math.Round(result.UploadMbps, 2),
            LatencyMs = result.PingMs,
            DownloadLoadedLatencyMs = result.DownloadLatencyMs,
            UploadLoadedLatencyMs = result.UploadLatencyMs,
            IdleDownloadMbps = Math.Round(idle.DownloadMbps, 3),
            IdleUploadMbps = Math.Round(idle.UploadMbps, 3),
            IdleSource = idle.Source,
            LiftDownloadMbps = lift?.DownloadProbeMbps,
            LiftUploadMbps = lift?.UploadProbeMbps,
            Success = result.Success,
            Error = result.Success ? null : Trim(result.ErrorMessage),
        };
        var limitedDown = result.Success && lift != null && IsProbeLimited(result.DownloadMbps, lift.DownloadProbeMbps);
        var limitedUp = result.Success && lift != null && IsProbeLimited(result.UploadMbps, lift.UploadProbeMbps);
        sample.ProbeLimited = limitedDown || limitedUp;
        await _repo.AddSampleAsync(sample, ct);

        if (!result.Success)
            return await FailAsync(profile, result.ErrorMessage ?? "The gateway speed test reported a failure.", ct);

        // A direction that hit its lift gets a higher lift next time, up to the link ceiling.
        if (limitedDown)
            profile.LiftDownloadMbps = Math.Max(profile.LiftDownloadMbps ?? 0, NextLift(result.DownloadMbps, ceiling, DownloadLiftRaiseFactor));
        if (limitedUp)
            profile.LiftUploadMbps = Math.Max(profile.LiftUploadMbps ?? 0, NextLift(result.UploadMbps, ceiling, UploadLiftRaiseFactor));

        profile.LastSampleAt = sample.SampledAt;
        profile.LastError = null;
        profile.ConsecutiveFailures = 0;
        profile.Interface = iface;
        await RecomputeAsync(profile, ct);

        var summary = $"Sample {sample.DownloadMbps:F0} / {sample.UploadMbps:F0} Mbps · " +
                      $"{profile.ValidSampleCount} samples, {profile.CoveragePercent:F0}% of the week covered";
        _logger.LogInformation("Adaptive SQM learning sample for WAN {Wan} ({Iface}): {Summary}", cfg.WanNumber, iface, summary);
        return new ScheduleRunOutcome(true, summary, null, Notify: false);

        // A deferral is recorded as "waiting", never "success": the row must not read as a result.
        static ScheduleRunOutcome Wait(string why) =>
            new(true, why, null, NextRunAt: DateTime.UtcNow + BusyRetry, Notify: false, Status: "waiting");
    }

    /// <summary>Re-learns the profile from every sample of the current run and stores it.</summary>
    public async Task RecomputeAsync(SqmCongestionProfile profile, CancellationToken ct)
    {
        var rows = await _repo.GetSamplesAsync(profile.WanNumber, profile.LearningStartedAt, ct);
        var samples = rows.Where(r => r.Success)
            .Select(r => new LearningSample(r.Id, r.LocalDayOfWeek, r.LocalHour, r.DownloadMbps, r.UploadMbps, r.SampledAt, r.ProbeLimited))
            .ToList();

        var learned = CongestionProfileLearner.Learn(samples);
        var exclusions = learned.Exclusions.ToDictionary(e => (int)e.SampleId, e => e.Reason);
        await _repo.SetSampleExclusionsAsync(profile.WanNumber, exclusions, ct);

        var p = learned.Profile;
        profile.DownloadMultipliersJson = JsonSerializer.Serialize(p.DownloadMultipliers);
        profile.UploadMultipliersJson = JsonSerializer.Serialize(p.UploadMultipliers);
        profile.SampleCountsJson = JsonSerializer.Serialize(p.SampleCounts);
        profile.PeakDownloadMbps = p.PeakDownloadMbps;
        profile.PeakUploadMbps = p.PeakUploadMbps;
        profile.ValidSampleCount = p.ValidSampleCount;
        profile.CoveragePercent = Math.Round(p.Coverage * 100, 1);
        profile.DaysSpanned = p.DaysSpanned;
        profile.IsReliable = p.IsReliable;
        profile.PeakIsLowerBound = p.PeakIsLowerBound;
        profile.ProfileUpdatedAt = DateTime.UtcNow;
        await _repo.SaveProfileAsync(profile, ct);
    }

    private async Task<ScheduleRunOutcome> CompleteAsync(int taskId, SqmCongestionProfile profile, CancellationToken ct)
    {
        await RecomputeAsync(profile, ct);
        profile.LearningCompletedAt = DateTime.UtcNow;
        await _repo.SaveProfileAsync(profile, ct);

        var task = await _schedules.GetByIdAsync(taskId, ct);
        if (task != null && task.Enabled)
        {
            task.Enabled = false;
            await _schedules.UpdateAsync(task, ct);
        }

        var summary = profile.IsReliable
            ? $"Learning complete: {profile.ValidSampleCount} samples over {profile.DaysSpanned} days, peak {profile.PeakDownloadMbps:F0} / {profile.PeakUploadMbps:F0} Mbps"
            : $"Learning ended with {profile.ValidSampleCount} samples covering {profile.CoveragePercent:F0}% of the week - not enough to rely on yet";
        _logger.LogInformation("Adaptive SQM learning for WAN {Wan} finished: {Summary}", profile.WanNumber, summary);
        return new ScheduleRunOutcome(true, summary, null, Notify: true);
    }

    private async Task<ScheduleRunOutcome> FailAsync(SqmCongestionProfile profile, string error, CancellationToken ct)
    {
        profile.ConsecutiveFailures++;
        profile.LastError = Trim(error);
        try { await _repo.SaveProfileAsync(profile, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not record the learning failure for WAN {Wan}", profile.WanNumber); }

        var n = profile.ConsecutiveFailures;
        var notify = n == FailureAlertThreshold || (n > FailureAlertThreshold && n % FailureAlertRepeatEvery == 0);
        _logger.LogWarning("Adaptive SQM learning sample failed for WAN {Wan} ({Count} in a row): {Error}", profile.WanNumber, n, error);
        return new ScheduleRunOutcome(false, null, error, Notify: notify);
    }

    /// <summary>
    /// The same download probe the deployed Adaptive SQM speed test uses (3% above the highest rate
    /// the WAN ever shapes to) and an upload lift 10% above nominal. The link stays shaped during
    /// the sample, so a learning run never turns bufferbloat loose for its test window; a sample
    /// that clips at this probe raises it for the next one (<see cref="RaiseLift"/>). Never above
    /// the link ceiling. Null when the WAN has no configuration to size from.
    /// </summary>
    internal static SqmShaperLift? BuildLift(SqmWanConfiguration? wanConfig)
    {
        if (wanConfig == null || wanConfig.NominalDownloadMbps <= 0)
            return null;

        var nominalUp = Math.Max(1, wanConfig.NominalUploadMbps);
        var sqm = new SqmConfig
        {
            ConnectionType = (ConnectionType)wanConfig.ConnectionType,
            NominalDownloadSpeed = wanConfig.NominalDownloadMbps,
            NominalUploadSpeed = nominalUp,
        };
        sqm.ApplyProfileSettings(wanConfig.LinkSpeedOverrideMbps);

        var down = sqm.SpeedtestProbeRateMbps;
        var up = Math.Max(nominalUp + 1, (int)Math.Ceiling(nominalUp * UploadLiftHeadroom));
        if (LinkCeiling(wanConfig) is { } ceiling)
        {
            down = Math.Min(down, ceiling);
            up = Math.Min(up, ceiling);
        }
        return new SqmShaperLift(down, up, wanConfig.RateProportionalDownloadBurst);
    }

    /// <summary>
    /// True when one of the WAN's two scheduled Adaptive SQM speed tests (saved as gateway-local
    /// times) is due within <paramref name="windowMinutes"/> of the gateway's clock.
    /// </summary>
    internal static bool ScheduledProbeImminent(int hour, int minute, SqmWanConfiguration wanConfig, int windowMinutes)
    {
        var now = hour * 60 + minute;
        foreach (var due in new[]
                 {
                     wanConfig.SpeedtestMorningHour * 60 + wanConfig.SpeedtestMorningMinute,
                     wanConfig.SpeedtestEveningHour * 60 + wanConfig.SpeedtestEveningMinute,
                 })
        {
            var ahead = ((due - now) % 1440 + 1440) % 1440;
            if (ahead <= windowMinutes) return true;
        }
        return false;
    }

    /// <summary>The highest lift the WAN allows: link speed with HTB headroom, or null when unknown.</summary>
    internal static int? LinkCeiling(SqmWanConfiguration wanConfig) =>
        wanConfig.LinkSpeedOverrideMbps is > 0 ? (int)(wanConfig.LinkSpeedOverrideMbps.Value * 0.98) : null;

    /// <summary>Whether a measured direction ran into its lift rather than the line.</summary>
    internal static bool IsProbeLimited(double measuredMbps, int liftMbps) =>
        liftMbps > 0 && measuredMbps >= liftMbps * ProbeLimitedFraction;

    /// <summary>The lift to use after a direction clipped at <paramref name="clippedMbps"/>.</summary>
    internal static int NextLift(double clippedMbps, int? ceiling, double raiseFactor) =>
        Math.Min((int)Math.Ceiling(clippedMbps * raiseFactor), ceiling ?? MaxLiftMbps);

    /// <summary>The configured lift raised to any remembered per-direction lift, never above the ceiling.</summary>
    internal static SqmShaperLift? RaiseLift(SqmShaperLift? baseLift, int? liftDown, int? liftUp, int? ceiling)
    {
        if (baseLift == null) return null;
        var down = Math.Max(baseLift.DownloadProbeMbps, liftDown ?? 0);
        var up = Math.Max(baseLift.UploadProbeMbps, liftUp ?? 0);
        if (ceiling is > 0)
        {
            down = Math.Min(down, ceiling.Value);
            up = Math.Min(up, ceiling.Value);
        }
        return baseLift with { DownloadProbeMbps = down, UploadProbeMbps = up };
    }

    private async Task<string?> ResolveWanGroupAsync(string iface)
    {
        try
        {
            var wans = await _sqmService.GetWanInterfacesFromControllerAsync();
            return wans.FirstOrDefault(w => w.Interface.Equals(iface, StringComparison.OrdinalIgnoreCase))?.NetworkGroup;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the WAN group for {Iface}", iface);
            return null;
        }
    }

    private static string FormatMbps(double mbps) => mbps >= 1000 ? $"{mbps / 1000:F1} Gbps" : $"{mbps:F1} Mbps";

    private static string Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no detail)";
        var t = text.Trim();
        return t.Length > 480 ? t[..480] + "..." : t;
    }
}
