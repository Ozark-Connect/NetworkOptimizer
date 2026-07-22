using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for Starlink terminal configurations
/// </summary>
public interface IStarlinkRepository
{
    Task<List<StarlinkConfiguration>> GetStarlinkConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<List<StarlinkConfiguration>> GetEnabledStarlinkConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<StarlinkConfiguration?> GetStarlinkConfigurationAsync(int id, CancellationToken cancellationToken = default);
    Task SaveStarlinkConfigurationAsync(StarlinkConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteStarlinkConfigurationAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggle only the <see cref="StarlinkConfiguration.Enabled"/> flag of one config (the
    /// row-level Disable/Enable button), without touching the rest of the entity. Bumps
    /// UpdatedAt; when disabling, clears the stale LastError so a paused row does not keep
    /// showing an old poll failure. No-op if the id does not exist.
    /// </summary>
    Task SetStarlinkEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist a poll outcome for one config, updating only LastPolled (when provided),
    /// LastError, and UpdatedAt - never Enabled. Skips (and returns false) when the config
    /// was disabled meanwhile, so an in-flight poll can neither resurrect a paused terminal
    /// nor overwrite its frozen state. Returns true when the result was persisted.
    /// </summary>
    Task<bool> UpdateStarlinkPollResultAsync(int id, DateTime? lastPolled, string? lastError, CancellationToken cancellationToken = default);
}
