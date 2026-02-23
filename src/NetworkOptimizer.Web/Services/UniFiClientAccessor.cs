using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Provides access to the UniFi API client via UniFiConnectionService,
/// implementing the interface defined in the Threats project to avoid circular references.
/// </summary>
public class UniFiClientAccessor : IUniFiClientAccessor
{
    private readonly UniFiConnectionService _connectionService;

    public UniFiClientAccessor(UniFiConnectionService connectionService)
    {
        _connectionService = connectionService;
    }

    public UniFiApiClient? Client => _connectionService.Client;

    public bool IsConnected => _connectionService.IsConnected;
}
