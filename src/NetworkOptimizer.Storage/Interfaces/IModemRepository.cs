using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for modem configurations
/// </summary>
public interface IModemRepository
{
    Task<List<ModemConfiguration>> GetModemConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<List<ModemConfiguration>> GetEnabledModemConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<ModemConfiguration?> GetModemConfigurationAsync(int id, CancellationToken cancellationToken = default);
    Task SaveModemConfigurationAsync(ModemConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteModemConfigurationAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggle only the <see cref="ModemConfiguration.Enabled"/> flag of one config (the
    /// row-level Disable/Enable button), without touching the rest of the entity. Bumps
    /// UpdatedAt; when disabling, clears the stale LastError so a paused row does not keep
    /// showing an old poll failure. No-op if the id does not exist.
    /// </summary>
    Task SetModemEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist a poll outcome for one config, updating only LastPolled (when provided),
    /// LastError, and UpdatedAt - never Enabled. Skips (and returns false) when the config
    /// was disabled meanwhile, so an in-flight poll can neither resurrect a paused modem nor
    /// overwrite its frozen state. Returns true when the result was persisted.
    ///
    /// <paramref name="detectedModel"/> fills in ModemType when it still holds the
    /// provider's generic placeholder, so a model the modem reports about itself replaces
    /// a stand-in like "GL-iNet". A model the user typed is never overwritten.
    /// </summary>
    Task<bool> UpdateModemPollResultAsync(int id, DateTime? lastPolled, string? lastError, string? detectedModel = null, CancellationToken cancellationToken = default);
}
