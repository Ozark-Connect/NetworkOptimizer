using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for cable modem configurations
/// </summary>
public interface ICmRepository
{
    Task<List<CmConfiguration>> GetCmConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<List<CmConfiguration>> GetEnabledCmConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<CmConfiguration?> GetCmConfigurationAsync(int id, CancellationToken cancellationToken = default);
    Task SaveCmConfigurationAsync(CmConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteCmConfigurationAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggle only the <see cref="CmConfiguration.Enabled"/> flag of one config (the
    /// row-level Disable/Enable button), without touching the rest of the entity. Bumps
    /// UpdatedAt; when disabling, clears the stale LastError so a paused row does not keep
    /// showing an old poll failure. No-op if the id does not exist.
    /// </summary>
    Task SetCmEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist a poll outcome for one config, updating only LastPolled (when provided),
    /// LastError, and UpdatedAt - never Enabled. Skips (and returns false) when the config
    /// was disabled meanwhile, so an in-flight poll can neither resurrect a paused CM nor
    /// overwrite its frozen state. Returns true when the result was persisted.
    /// </summary>
    Task<bool> UpdateCmPollResultAsync(int id, DateTime? lastPolled, string? lastError, CancellationToken cancellationToken = default);
}
