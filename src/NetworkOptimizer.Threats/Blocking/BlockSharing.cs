using NetworkOptimizer.Threats.Interfaces;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>
/// Which sites' blocks a site enforces. This is the sharing boundary: the public build shares
/// across every opted-in site on the instance, and a multi-tenant host narrows it by wrapping this
/// interface (never across tenants: a shared list leaks one customer's attackers to another and lets
/// one tenant's false positive block traffic at another's edge).
/// </summary>
public interface IBlockSharingScope
{
    /// <summary>
    /// The sites whose active blocks <paramref name="site"/> enforces, itself included. Null is the
    /// default site, both as input and in the result.
    /// </summary>
    Task<IReadOnlyList<string?>> GetPeerSitesAsync(string? site, CancellationToken cancellationToken);
}

/// <summary>Shares among every site on the instance that has "Share blocks" on.</summary>
public sealed class InstanceBlockSharingScope : IBlockSharingScope
{
    private readonly IThreatSettingsAccessor _settings;
    private readonly NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? _sites;

    public InstanceBlockSharingScope(IThreatSettingsAccessor settings,
        NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? sites = null)
    {
        _settings = settings;
        _sites = sites;
    }

    public async Task<IReadOnlyList<string?>> GetPeerSitesAsync(string? site, CancellationToken cancellationToken)
    {
        if (_sites == null || !await SharesAsync(site, cancellationToken))
            return [site];
        var peers = new List<string?>();
        foreach (var key in await _sites.GetSiteKeysAsync(cancellationToken))
        {
            var peer = key == _sites.DefaultKey ? null : key;
            if (peer == site || await SharesAsync(peer, cancellationToken))
                peers.Add(peer);
        }
        return peers;
    }

    // Same default as BlockingSettings.Share: on unless the site opted out.
    private async Task<bool> SharesAsync(string? site, CancellationToken ct) =>
        !bool.TryParse(await _settings.GetSettingAsync(BlockingSettingKeys.ForSite(BlockingSettingKeys.Share, site), ct), out var on) || on;
}
