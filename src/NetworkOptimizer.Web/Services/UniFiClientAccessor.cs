using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Provides access to the UniFi API client via UniFiConnectionService,
/// implementing the interface defined in the Threats project to avoid circular references.
/// </summary>
public class UniFiClientAccessor : IUniFiClientAccessor
{
    private readonly SiteConnectionRegistry _siteConnections;

    public UniFiClientAccessor(SiteConnectionRegistry siteConnections)
    {
        _siteConnections = siteConnections;
    }

    public UniFiApiClient? Client => _siteConnections.GetDefault().Client;

    public bool IsConnected => _siteConnections.GetDefault().IsConnected;

    public UniFiApiClient? GetClient(string? siteSlug) => Connection(siteSlug).Client;

    public bool GetIsConnected(string? siteSlug) => Connection(siteSlug).IsConnected;

    private UniFiConnectionService Connection(string? siteSlug) =>
        string.IsNullOrEmpty(siteSlug)
            ? _siteConnections.GetDefault()
            : _siteConnections.GetFor(siteSlug);
}
