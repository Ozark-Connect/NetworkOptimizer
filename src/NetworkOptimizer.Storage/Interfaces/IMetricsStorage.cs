namespace NetworkOptimizer.Storage.Interfaces;

public interface IMetricsStorage
{
    Task WriteMetricsAsync(string deviceId, string measurementType, Dictionary<string, object> metrics, Dictionary<string, string>? tags = null, CancellationToken cancellationToken = default);
    Task WriteInterfaceMetricsAsync(string deviceId, string interfaceId, Dictionary<string, object> metrics, Dictionary<string, string>? tags = null, CancellationToken cancellationToken = default);
    Task WriteSqmMetricsAsync(string deviceId, Dictionary<string, object> metrics, Dictionary<string, string>? tags = null, CancellationToken cancellationToken = default);
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
}
