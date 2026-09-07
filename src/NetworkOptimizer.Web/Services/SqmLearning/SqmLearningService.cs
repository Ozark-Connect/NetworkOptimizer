using System.Text.Json;
using NetworkOptimizer.Alerts;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Sqm.Models;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.SqmLearning;

/// <summary>
/// Adaptive SQM congestion profile learning for the site in context. The schedule task is the
/// single source of truth for whether learning is running; the profile row holds the data.
/// </summary>
public class SqmLearningService : ISqmLearningService
{
    private readonly ISqmLearningRepository _repo;
    private readonly IScheduleRepository _schedules;
    private readonly ScheduleService _scheduleService;
    private readonly SiteContextService _siteContext;
    private readonly ILogger<SqmLearningService> _logger;

    public SqmLearningService(
        ISqmLearningRepository repo,
        IScheduleRepository schedules,
        ScheduleService scheduleService,
        SiteContextService siteContext,
        ILogger<SqmLearningService> logger)
    {
        _repo = repo;
        _schedules = schedules;
        _scheduleService = scheduleService;
        _siteContext = siteContext;
        _logger = logger;
    }

    public async Task<SqmLearningStatus?> GetStatusAsync(int wanNumber)
    {
        var profile = await _repo.GetProfileAsync(wanNumber);
        if (profile == null) return null;
        return await BuildStatusAsync(profile);
    }

    public async Task<List<SqmLearningSample>> GetSamplesAsync(int wanNumber, int count = 200)
    {
        var profile = await _repo.GetProfileAsync(wanNumber);
        var samples = await _repo.GetSamplesAsync(wanNumber, profile?.LearningStartedAt);
        return samples.OrderByDescending(s => s.SampledAt).Take(Math.Max(1, count)).ToList();
    }

    public async Task<SqmLearningStatus> StartAsync(SqmLearningStartRequest request)
    {
        if (request.WanNumber <= 0) throw new ArgumentException("WAN number is required", nameof(request));
        var ifaceCheck = NetworkOptimizer.Sqm.InputSanitizer.ValidateInterface(request.Interface);
        if (!ifaceCheck.isValid) throw new ArgumentException(ifaceCheck.error, nameof(request));

        var profile = await _repo.GetProfileAsync(request.WanNumber);
        if (profile != null)
        {
            var existing = await FindTaskAsync(profile);
            if (existing is { Enabled: true } && profile.LearningEndsAt > DateTime.UtcNow && profile.LearningCompletedAt == null)
                return await BuildStatusAsync(profile);
        }

        // A new run starts clean: the previous run's samples describe a week that is over.
        await _repo.DeleteSamplesAsync(request.WanNumber);
        if (profile != null && await FindTaskAsync(profile) is { } stale)
            await _schedules.DeleteAsync(stale.Id);

        var now = DateTime.UtcNow;
        var days = Math.Clamp(request.Days, 1, 28);
        var duration = Math.Clamp(request.DurationSeconds, 2, 20);
        if (request.NominalDownloadMbps <= 0 || request.NominalUploadMbps <= 0)
            throw new ArgumentException("Nominal download and upload speeds are required to size the measurement", nameof(request));

        var cfg = new SqmLearningTaskConfig
        {
            WanNumber = request.WanNumber,
            WanName = request.Name,
            WanGroup = request.WanGroup,
            ConnectionType = (int)request.ConnectionType,
            EndsAt = now.AddDays(days),
            DurationSeconds = duration,
            NominalDownloadMbps = request.NominalDownloadMbps,
            NominalUploadMbps = request.NominalUploadMbps,
            LinkSpeedOverrideMbps = request.LinkSpeedOverrideMbps,
            RateProportionalDownloadBurst = request.RateProportionalDownloadBurst,
        };

        var task = new ScheduledTask
        {
            TaskType = ScheduleService.SqmLearningTaskType,
            Name = $"Adaptive SQM Learning ({request.Name})",
            Enabled = true,
            FrequencyMinutes = 60,
            TargetId = request.Interface,
            TargetConfig = cfg.ToJson(),
            // The first sample follows promptly so the user sees the run is alive.
            NextRunAt = now.AddMinutes(1),
        };
        var taskId = await _schedules.SaveAsync(task);

        profile ??= new SqmCongestionProfile { WanNumber = request.WanNumber };
        profile.Interface = request.Interface;
        profile.Name = request.Name;
        profile.ConnectionType = (int)request.ConnectionType;
        profile.ScheduledTaskId = taskId;
        profile.LearningStartedAt = now;
        profile.LearningEndsAt = cfg.EndsAt;
        profile.LearningCompletedAt = null;
        profile.SampleDurationSeconds = duration;
        profile.LastSampleAt = null;
        profile.LastError = null;
        profile.ConsecutiveFailures = 0;
        profile.DownloadMultipliersJson = null;
        profile.UploadMultipliersJson = null;
        profile.SampleCountsJson = null;
        profile.PeakDownloadMbps = 0;
        profile.PeakUploadMbps = 0;
        profile.ValidSampleCount = 0;
        profile.CoveragePercent = 0;
        profile.DaysSpanned = 0;
        profile.IsReliable = false;
        profile.ProfileUpdatedAt = null;
        await _repo.SaveProfileAsync(profile);

        _logger.LogInformation("Adaptive SQM learning started for WAN {Wan} ({Iface}) on site {Site}, ends {Ends:u}",
            request.WanNumber, request.Interface, _siteContext.Slug, cfg.EndsAt);
        return await BuildStatusAsync(profile);
    }

    public async Task StopAsync(int wanNumber)
    {
        var profile = await _repo.GetProfileAsync(wanNumber);
        if (profile == null) return;

        var task = await FindTaskAsync(profile);
        if (task is { Enabled: true })
        {
            task.Enabled = false;
            await _schedules.UpdateAsync(task);
        }
        if (profile.LearningCompletedAt == null && profile.LearningStartedAt != null)
        {
            profile.LearningCompletedAt = DateTime.UtcNow;
            await _repo.SaveProfileAsync(profile);
        }
        _logger.LogInformation("Adaptive SQM learning stopped for WAN {Wan} on site {Site}", wanNumber, _siteContext.Slug);
    }

    public async Task ClearAsync(int wanNumber)
    {
        var profile = await _repo.GetProfileAsync(wanNumber);
        if (profile != null && await FindTaskAsync(profile) is { } task)
            await _schedules.DeleteAsync(task.Id);
        await _repo.DeleteProfileAsync(wanNumber);
        _logger.LogInformation("Adaptive SQM learned profile cleared for WAN {Wan} on site {Site}", wanNumber, _siteContext.Slug);
    }

    public async Task<bool> RunSampleNowAsync(int wanNumber)
    {
        var profile = await _repo.GetProfileAsync(wanNumber);
        if (profile == null) return false;
        var task = await FindTaskAsync(profile);
        if (task == null) return false;
        return await _scheduleService.RunNowAsync(task.Id, _siteContext.Slug);
    }

    public async Task<string?> ExportProfileJsonAsync(int wanNumber)
    {
        var profile = await _repo.GetProfileAsync(wanNumber);
        if (profile == null || !profile.HasProfile) return null;
        var learned = ToLearned(profile);
        var export = new
        {
            format = "network-optimizer-congestion-profile",
            version = 1,
            generatedAt = DateTime.UtcNow,
            connectionType = ((ConnectionType)profile.ConnectionType).ToString(),
            name = profile.Name,
            learnedFrom = profile.LearningStartedAt,
            learnedTo = profile.LearningCompletedAt ?? profile.ProfileUpdatedAt,
            validSamples = profile.ValidSampleCount,
            coveragePercent = profile.CoveragePercent,
            daysSpanned = profile.DaysSpanned,
            reliable = profile.IsReliable,
            peakDownloadMbps = profile.PeakDownloadMbps,
            peakUploadMbps = profile.PeakUploadMbps,
            peakIsLowerBound = profile.PeakIsLowerBound,
            slotLayout = "index = day * 24 + hour, day 0 = Monday",
            downloadMultipliers = learned?.DownloadMultipliers,
            uploadMultipliers = learned?.UploadMultipliers,
            sampleCounts = learned?.SampleCounts,
        };
        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The learned curves stored on a profile row, or null when it has none yet.</summary>
    public static LearnedCongestionProfile? ToLearned(SqmCongestionProfile profile)
    {
        if (!profile.HasProfile) return null;
        try
        {
            var down = JsonSerializer.Deserialize<double[]>(profile.DownloadMultipliersJson!);
            var up = string.IsNullOrEmpty(profile.UploadMultipliersJson) ? null : JsonSerializer.Deserialize<double[]>(profile.UploadMultipliersJson);
            var counts = string.IsNullOrEmpty(profile.SampleCountsJson) ? null : JsonSerializer.Deserialize<int[]>(profile.SampleCountsJson);
            if (down == null || down.Length != LearnedCongestionProfile.Slots) return null;
            return new LearnedCongestionProfile
            {
                DownloadMultipliers = down,
                UploadMultipliers = up is { Length: LearnedCongestionProfile.Slots } ? up : down,
                SampleCounts = counts is { Length: LearnedCongestionProfile.Slots } ? counts : new int[LearnedCongestionProfile.Slots],
                PeakDownloadMbps = profile.PeakDownloadMbps,
                PeakUploadMbps = profile.PeakUploadMbps,
                ValidSampleCount = profile.ValidSampleCount,
                DaysSpanned = profile.DaysSpanned,
                IsReliable = profile.IsReliable,
                PeakIsLowerBound = profile.PeakIsLowerBound,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ScheduledTask?> FindTaskAsync(SqmCongestionProfile profile)
    {
        if (profile.ScheduledTaskId is int id)
        {
            var task = await _schedules.GetByIdAsync(id);
            if (task != null && task.TaskType == ScheduleService.SqmLearningTaskType) return task;
        }
        // The id can go stale (a task deleted from the Schedule tab and a new one made); fall back
        // to the newest learning task for this WAN.
        var all = await _schedules.GetAllAsync();
        return all.Where(t => t.TaskType == ScheduleService.SqmLearningTaskType)
            .Where(t => SqmLearningTaskConfig.Parse(t.TargetConfig)?.WanNumber == profile.WanNumber)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefault();
    }

    private async Task<SqmLearningStatus> BuildStatusAsync(SqmCongestionProfile profile)
    {
        var task = await FindTaskAsync(profile);
        var now = DateTime.UtcNow;
        var samples = await _repo.GetSamplesAsync(profile.WanNumber, profile.LearningStartedAt);
        var active = task is { Enabled: true } && profile.LearningCompletedAt == null && profile.LearningEndsAt > now;
        return new SqmLearningStatus
        {
            WanNumber = profile.WanNumber,
            Interface = profile.Interface,
            Name = profile.Name,
            HasProfile = profile.HasProfile,
            IsActive = active,
            IsCompleted = profile.LearningCompletedAt != null,
            TaskId = task?.Id,
            TaskEnabled = task?.Enabled ?? false,
            IsRunning = task != null && _scheduleService.IsTaskRunning(task.Id, _siteContext.Slug),
            StartedAt = profile.LearningStartedAt,
            EndsAt = profile.LearningEndsAt,
            CompletedAt = profile.LearningCompletedAt,
            LastRunAt = task?.LastRunAt,
            NextRunAt = active ? task?.NextRunAt : null,
            LastSampleAt = profile.LastSampleAt,
            LastStatus = task?.LastStatus,
            LastSummary = task?.LastResultSummary,
            LastError = profile.LastError ?? task?.LastErrorMessage,
            ValidSampleCount = profile.ValidSampleCount,
            TotalSampleCount = samples.Count,
            CoveragePercent = profile.CoveragePercent,
            DaysSpanned = profile.DaysSpanned,
            IsReliable = profile.IsReliable,
            PeakDownloadMbps = profile.PeakDownloadMbps,
            PeakUploadMbps = profile.PeakUploadMbps,
            DurationSeconds = profile.SampleDurationSeconds,
            ProbeLimitedSampleCount = samples.Count(s => s.Success && !s.Excluded && s.ProbeLimited),
            PeakIsLowerBound = profile.PeakIsLowerBound,
            LiftDownloadMbps = profile.LiftDownloadMbps,
            LiftUploadMbps = profile.LiftUploadMbps,
            LiftCeilingMbps = profile.LiftCeilingMbps,
            Profile = ToLearned(profile),
        };
    }
}
