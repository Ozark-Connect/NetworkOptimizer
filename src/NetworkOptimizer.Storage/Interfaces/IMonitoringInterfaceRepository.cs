using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for monitoring interfaces (ONT/modem management access routes
/// deployed to the gateway).
/// </summary>
public interface IMonitoringInterfaceRepository
{
    Task<List<MonitoringInterface>> GetMonitoringInterfacesAsync(CancellationToken cancellationToken = default);
    Task<MonitoringInterface?> GetMonitoringInterfaceAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the monitoring interface whose target IP matches the given address,
    /// if one exists. Used to surface deployment status next to ONT/modem monitor
    /// configs that share the same management IP.
    /// </summary>
    Task<MonitoringInterface?> GetByTargetIpAsync(string targetIp, CancellationToken cancellationToken = default);

    Task SaveMonitoringInterfaceAsync(MonitoringInterface config, CancellationToken cancellationToken = default);
    Task DeleteMonitoringInterfaceAsync(int id, CancellationToken cancellationToken = default);
}
