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
            return Wait($"Waiting: the Adaptive SQM speed test is running on {profile.Name}");
        if (!idle.IsIdle)
            return Wait($"Waiting for an idle stretch: {profile.Name} at {FormatMbps(idle.DownloadMbps)} down / {FormatMbps(idle.UploadMbps)} up");

        var options = new GatewayWanTestOptions
        {
            DurationSeconds = cfg.DurationSeconds,
            Streams = SampleStreams,
            Ephemeral = true,
            ShaperLift = BuildLift(wanConfig),
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
            return Wait($"Waiting: another WAN speed test is running");

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
            Success = result.Success,
            Error = result.Success ? null : Trim(result.ErrorMessage),
        };
        await _repo.AddSampleAsync(sample, ct);

        if (!result.Success)
            return await FailAsync(profile, result.ErrorMessage ?? "The gateway speed test reported a failure.", ct);

        profile.LastSampleAt = sample.SampledAt;
        profile.LastError = null;
        profile.ConsecutiveFailures = 0;
        profile.Interface = iface;
        await RecomputeAsync(profile, ct);

        var summary = $"Sample {sample.DownloadMbps:F0} / {sample.UploadMbps:F0} Mbps · " +
                      $"{profile.ValidSampleCount} samples, {profile.CoveragePercent:F0}% of the week covered";
        _logger.LogInformation("Adaptive SQM learning sample for WAN {Wan} ({Iface}): {Summary}", cfg.WanNumber, iface, summary);
        return new ScheduleRunOutcome(true, summary, null, Notify: false);

        static ScheduleRunOutcome Wait(string why) =>
            new(true, why, null, NextRunAt: DateTime.UtcNow + BusyRetry, Notify: false);
    }

    /// <summary>Re-learns the profile from every sample of the current run and stores it.</summary>
    public async Task RecomputeAsync(SqmCongestionProfile profile, CancellationToken ct)
    {
        var rows = await _repo.GetSamplesAsync(profile.WanNumber, profile.LearningStartedAt, ct);
        var samples = rows.Where(r => r.Success)
            .Select(r => new LearningSample(r.Id, r.LocalDayOfWeek, r.LocalHour, r.DownloadMbps, r.UploadMbps, r.SampledAt))
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
    /// Probe rates that stay clear of the line rate: at least twice nominal so a conservative
    /// nominal cannot cap the measurement, at least the deploy's own probe rate, and never above
    /// the link ceiling the deploy uses. Null when the WAN has no saved configuration to size from.
    /// </summary>
    internal static SqmShaperLift? BuildLift(SqmWanConfiguration? wanConfig)
    {
        if (wanConfig == null || wanConfig.NominalDownloadMbps <= 0)
            return null;

        var sqm = new SqmConfig
        {
            ConnectionType = (ConnectionType)wanConfig.ConnectionType,
            NominalDownloadSpeed = wanConfig.NominalDownloadMbps,
            NominalUploadSpeed = Math.Max(1, wanConfig.NominalUploadMbps),
        };
        sqm.ApplyProfileSettings(wanConfig.LinkSpeedOverrideMbps);

        var down = Math.Max(sqm.SpeedtestProbeRateMbps, wanConfig.NominalDownloadMbps * 2);
        var up = Math.Max(10, Math.Max(1, wanConfig.NominalUploadMbps) * 2);
        if (wanConfig.LinkSpeedOverrideMbps is > 0)
        {
            var ceiling = (int)(wanConfig.LinkSpeedOverrideMbps.Value * 0.98);
            down = Math.Min(down, ceiling);
            up = Math.Min(up, ceiling);
        }
        return new SqmShaperLift(down, up, wanConfig.RateProportionalDownloadBurst);
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
