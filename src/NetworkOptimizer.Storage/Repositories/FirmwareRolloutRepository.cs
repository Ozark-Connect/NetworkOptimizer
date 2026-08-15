using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for Firmware Rollout settings, plans, steps, and learned model timings.
/// </summary>
public class FirmwareRolloutRepository : IFirmwareRolloutRepository
{
    /// <summary>
    /// How many raw downtime samples per model the percentile window keeps. Old samples
    /// fall off so a model's estimate follows its current firmware line rather than years
    /// of history; SampleCount still counts every measurement ever taken.
    /// </summary>
    private const int TimingSampleWindow = 50;

    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<FirmwareRolloutRepository> _logger;

    public FirmwareRolloutRepository(NetworkOptimizerDbContext context, ILogger<FirmwareRolloutRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<FirmwareRolloutSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.FirmwareRolloutSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
            if (existing != null)
                return existing;

            var created = new FirmwareRolloutSettings { UpdatedAt = DateTime.UtcNow };
            _context.FirmwareRolloutSettings.Add(created);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Created default firmware rollout settings");
            return created;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get firmware rollout settings");
            throw;
        }
    }

    public async Task SaveSettingsAsync(FirmwareRolloutSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var existing = await _context.FirmwareRolloutSettings.FirstOrDefaultAsync(cancellationToken);
            if (existing != null)
            {
                existing.Mode = settings.Mode;
                existing.GlobalChannel = settings.GlobalChannel;
                existing.PerDeviceTypeChannelsJson = settings.PerDeviceTypeChannelsJson;
                existing.PerSkuChannelsJson = settings.PerSkuChannelsJson;
                existing.NetworkAppChannel = settings.NetworkAppChannel;
                existing.UniFiOsChannel = settings.UniFiOsChannel;
                existing.IncludeUniFiOs = settings.IncludeUniFiOs;
                existing.IncludeUniFiNetwork = settings.IncludeUniFiNetwork;
                existing.ExclusionsJson = settings.ExclusionsJson;
                existing.SpacingProfile = settings.SpacingProfile;
                existing.AdvancedSpacingJson = settings.AdvancedSpacingJson;
                existing.SuppressStandardAlerts = settings.SuppressStandardAlerts;
                existing.AutopilotWindowMode = settings.AutopilotWindowMode;
                existing.FixedDayOfWeek = settings.FixedDayOfWeek;
                existing.FixedHour = settings.FixedHour;
                existing.NotifyHoursAhead = settings.NotifyHoursAhead;
                existing.SoakHours = settings.SoakHours;
                existing.MinReleaseAgeDays = settings.MinReleaseAgeDays;
                existing.WaiveBackup = settings.WaiveBackup;
                existing.PerWaveApproval = settings.PerWaveApproval;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                settings.UpdatedAt = DateTime.UtcNow;
                _context.FirmwareRolloutSettings.Add(settings);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Saved firmware rollout settings (Mode={Mode})", settings.Mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save firmware rollout settings");
            throw;
        }
    }

    public async Task<FirmwareRolloutPlan> CreatePlanAsync(FirmwareRolloutPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            plan.CreatedAt = DateTime.UtcNow;
            _context.FirmwareRolloutPlans.Add(plan);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Created firmware rollout plan {Id} ({Status})", plan.Id, plan.Status);
            return plan;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create firmware rollout plan");
            throw;
        }
    }

    public async Task<FirmwareRolloutPlan?> GetActivePlanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.FirmwareRolloutPlans
                .AsNoTracking()
                .Where(p => !FirmwareRolloutStatuses.Terminal.Contains(p.Status))
                .OrderByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active firmware rollout plan");
            throw;
        }
    }

    public async Task<FirmwareRolloutPlan?> GetPlanAsync(int planId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.FirmwareRolloutPlans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get firmware rollout plan {Id}", planId);
            throw;
        }
    }

    public async Task<List<FirmwareRolloutPlan>> GetPlanHistoryAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.FirmwareRolloutPlans
                .AsNoTracking()
                .OrderByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.Id)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get firmware rollout plan history");
            throw;
        }
    }

    public async Task UpdatePlanAsync(FirmwareRolloutPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            var existing = await _context.FirmwareRolloutPlans
                .FirstOrDefaultAsync(p => p.Id == plan.Id, cancellationToken);
            if (existing == null)
            {
                _logger.LogWarning("Firmware rollout plan {Id} no longer exists; update skipped", plan.Id);
                return;
            }

            existing.Status = plan.Status;
            existing.ScheduledStartAt = plan.ScheduledStartAt;
            existing.StartedAt = plan.StartedAt;
            existing.CompletedAt = plan.CompletedAt;
            existing.PlanJson = plan.PlanJson;
            existing.OriginalChannelSettingsJson = plan.OriginalChannelSettingsJson;
            existing.ReportJson = plan.ReportJson;
            existing.CreatedBy = plan.CreatedBy;

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Updated firmware rollout plan {Id} to {Status}", plan.Id, plan.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update firmware rollout plan {Id}", plan.Id);
            throw;
        }
    }

    public async Task AddStepsAsync(IEnumerable<FirmwareRolloutStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);

        try
        {
            var list = steps.ToList();
            if (list.Count == 0)
                return;

            _context.FirmwareRolloutSteps.AddRange(list);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Added {Count} firmware rollout steps", list.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add firmware rollout steps");
            throw;
        }
    }

    public async Task UpdateStepAsync(FirmwareRolloutStep step, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);

        try
        {
            var existing = await _context.FirmwareRolloutSteps
                .FirstOrDefaultAsync(s => s.Id == step.Id, cancellationToken);
            if (existing == null)
            {
                _logger.LogWarning("Firmware rollout step {Id} no longer exists; update skipped", step.Id);
                return;
            }

            existing.PlanId = step.PlanId;
            existing.DeviceMac = step.DeviceMac;
            existing.DeviceName = step.DeviceName;
            existing.Model = step.Model;
            existing.DeviceType = step.DeviceType;
            existing.FromVersion = step.FromVersion;
            existing.ToVersion = step.ToVersion;
            existing.Channel = step.Channel;
            existing.Wave = step.Wave;
            existing.State = step.State;
            existing.CommandedAt = step.CommandedAt;
            existing.WentDownAt = step.WentDownAt;
            existing.BackAt = step.BackAt;
            existing.DowntimeSeconds = step.DowntimeSeconds;
            existing.PreStatsJson = step.PreStatsJson;
            existing.PostStatsJson = step.PostStatsJson;
            existing.Error = step.Error;

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Updated firmware rollout step {Id} to {State}", step.Id, step.State);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update firmware rollout step {Id}", step.Id);
            throw;
        }
    }

    public async Task<List<FirmwareRolloutStep>> GetStepsAsync(int planId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.FirmwareRolloutSteps
                .AsNoTracking()
                .Where(s => s.PlanId == planId)
                .OrderBy(s => s.Wave)
                .ThenBy(s => s.Id)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get steps for firmware rollout plan {Id}", planId);
            throw;
        }
    }

    public async Task<FirmwareModelTiming> RecordModelTimingAsync(string model, int downtimeSeconds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required", nameof(model));

        try
        {
            var existing = await _context.FirmwareModelTimings
                .FirstOrDefaultAsync(t => t.Model == model, cancellationToken);
            if (existing == null)
            {
                existing = new FirmwareModelTiming { Model = model };
                _context.FirmwareModelTimings.Add(existing);
            }

            var samples = ParseSamples(existing.RecentSamplesJson);
            samples.Add(downtimeSeconds);
            if (samples.Count > TimingSampleWindow)
                samples.RemoveRange(0, samples.Count - TimingSampleWindow);

            existing.RecentSamplesJson = JsonSerializer.Serialize(samples);
            existing.SampleCount++;
            existing.MedianDowntimeSeconds = Median(samples);
            existing.P90DowntimeSeconds = Percentile(samples, 0.90);
            existing.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Recorded {Seconds}s downtime for {Model} (n={Count}, median={Median}s)",
                downtimeSeconds, model, existing.SampleCount, existing.MedianDowntimeSeconds);
            return existing;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record firmware timing for {Model}", model);
            throw;
        }
    }

    public async Task<List<FirmwareModelTiming>> GetModelTimingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.FirmwareModelTimings
                .AsNoTracking()
                .OrderBy(t => t.Model)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get firmware model timings");
            throw;
        }
    }

    public async Task<FirmwareModelTiming?> GetModelTimingAsync(string model, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.FirmwareModelTimings
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Model == model, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get firmware model timing for {Model}", model);
            throw;
        }
    }

    /// <summary>Reads the sample window, treating unreadable content as an empty window.</summary>
    private static List<int> ParseSamples(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<int>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Middle value, averaging the two middle values on an even count.</summary>
    private static int Median(List<int> samples)
    {
        var sorted = samples.Order().ToList();
        var n = sorted.Count;
        if (n == 0)
            return 0;

        return n % 2 == 1
            ? sorted[n / 2]
            : (int)Math.Round((sorted[n / 2 - 1] + sorted[n / 2]) / 2.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Nearest-rank percentile: the smallest sample at or above the requested share of the window.</summary>
    private static int Percentile(List<int> samples, double percentile)
    {
        var sorted = samples.Order().ToList();
        var n = sorted.Count;
        if (n == 0)
            return 0;

        var rank = (int)Math.Ceiling(percentile * n);
        return sorted[Math.Clamp(rank - 1, 0, n - 1)];
    }
}
