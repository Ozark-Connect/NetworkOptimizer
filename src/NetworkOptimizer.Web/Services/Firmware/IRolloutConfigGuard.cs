namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Change Control's hooks into a rollout: a pinned config snapshot before it starts, and a
/// config check after the UniFi Network application step that can hold the rollout.
/// </summary>
public interface IRolloutConfigGuard
{
    /// <summary>Pins a snapshot of the site's config before the rollout commands anything. Null when none was taken.</summary>
    Task<int?> PinBeforeRolloutAsync(string siteSlug, int planId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the config after the Network app update and returns why the rollout should pause
    /// (a security regression, or a change no admin made), or null to carry on.
    /// </summary>
    Task<string?> CheckAfterNetworkAppAsync(string siteSlug, DateTime rolloutStartedUtc, CancellationToken cancellationToken);
}
