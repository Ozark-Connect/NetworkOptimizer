using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for Adaptive SQM congestion profile learning (profile rows and raw samples).
/// </summary>
public class SqmLearningRepository : ISqmLearningRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<SqmLearningRepository> _logger;

    public SqmLearningRepository(NetworkOptimizerDbContext context, ILogger<SqmLearningRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<SqmCongestionProfile?> GetProfileAsync(int wanNumber, CancellationToken cancellationToken = default) =>
        _context.SqmCongestionProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.WanNumber == wanNumber, cancellationToken);

    public Task<List<SqmCongestionProfile>> GetAllProfilesAsync(CancellationToken cancellationToken = default) =>
        _context.SqmCongestionProfiles.AsNoTracking().OrderBy(p => p.WanNumber).ToListAsync(cancellationToken);

    public async Task SaveProfileAsync(SqmCongestionProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.SqmCongestionProfiles
                .FirstOrDefaultAsync(p => p.WanNumber == profile.WanNumber, cancellationToken);

            if (existing == null)
            {
                profile.CreatedAt = DateTime.UtcNow;
                profile.UpdatedAt = DateTime.UtcNow;
                _context.SqmCongestionProfiles.Add(profile);
            }
            else
            {
                existing.Interface = profile.Interface;
                existing.Name = profile.Name;
                existing.ConnectionType = profile.ConnectionType;
                existing.ScheduledTaskId = profile.ScheduledTaskId;
                existing.LearningStartedAt = profile.LearningStartedAt;
                existing.LearningEndsAt = profile.LearningEndsAt;
                existing.LearningCompletedAt = profile.LearningCompletedAt;
                existing.SampleDurationSeconds = profile.SampleDurationSeconds;
                existing.LastSampleAt = profile.LastSampleAt;
                existing.LastError = profile.LastError;
                existing.ConsecutiveFailures = profile.ConsecutiveFailures;
                existing.DownloadMultipliersJson = profile.DownloadMultipliersJson;
                existing.UploadMultipliersJson = profile.UploadMultipliersJson;
                existing.SampleCountsJson = profile.SampleCountsJson;
                existing.PeakDownloadMbps = profile.PeakDownloadMbps;
                existing.PeakUploadMbps = profile.PeakUploadMbps;
                existing.ValidSampleCount = profile.ValidSampleCount;
                existing.CoveragePercent = profile.CoveragePercent;
                existing.DaysSpanned = profile.DaysSpanned;
                existing.IsReliable = profile.IsReliable;
                existing.PeakIsLowerBound = profile.PeakIsLowerBound;
                existing.LiftDownloadMbps = profile.LiftDownloadMbps;
                existing.LiftUploadMbps = profile.LiftUploadMbps;
                existing.LiftCeilingMbps = profile.LiftCeilingMbps;
                existing.ProfileUpdatedAt = profile.ProfileUpdatedAt;
                existing.UpdatedAt = DateTime.UtcNow;
                profile.Id = existing.Id;
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save SQM congestion profile for WAN {WanNumber}", profile.WanNumber);
            throw;
        }
    }

    public async Task DeleteProfileAsync(int wanNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            await DeleteSamplesAsync(wanNumber, cancellationToken);
            var existing = await _context.SqmCongestionProfiles
                .FirstOrDefaultAsync(p => p.WanNumber == wanNumber, cancellationToken);
            if (existing != null)
            {
                _context.SqmCongestionProfiles.Remove(existing);
                await _context.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete SQM congestion profile for WAN {WanNumber}", wanNumber);
            throw;
        }
    }

    public async Task<int> AddSampleAsync(SqmLearningSample sample, CancellationToken cancellationToken = default)
    {
        _context.SqmLearningSamples.Add(sample);
        await _context.SaveChangesAsync(cancellationToken);
        return sample.Id;
    }

    public Task<List<SqmLearningSample>> GetSamplesAsync(int wanNumber, DateTime? sinceUtc = null, CancellationToken cancellationToken = default)
    {
        var query = _context.SqmLearningSamples.AsNoTracking().Where(s => s.WanNumber == wanNumber);
        if (sinceUtc.HasValue)
            query = query.Where(s => s.SampledAt >= sinceUtc.Value);
        return query.OrderBy(s => s.SampledAt).ToListAsync(cancellationToken);
    }

    public async Task SetSampleExclusionsAsync(int wanNumber, IReadOnlyDictionary<int, string> exclusions, CancellationToken cancellationToken = default)
    {
        var samples = await _context.SqmLearningSamples
            .Where(s => s.WanNumber == wanNumber)
            .ToListAsync(cancellationToken);
        foreach (var sample in samples)
        {
            if (exclusions.TryGetValue(sample.Id, out var reason))
            {
                sample.Excluded = true;
                sample.ExclusionReason = reason.Length > 200 ? reason[..200] : reason;
            }
            else
            {
                sample.Excluded = false;
                sample.ExclusionReason = null;
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteSamplesAsync(int wanNumber, CancellationToken cancellationToken = default)
    {
        var samples = await _context.SqmLearningSamples
            .Where(s => s.WanNumber == wanNumber)
            .ToListAsync(cancellationToken);
        if (samples.Count == 0) return;
        _context.SqmLearningSamples.RemoveRange(samples);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
