using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Core;
using InfluxDB.Client.Writes;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using System.Collections.Concurrent;

namespace NetworkOptimizer.Storage;

/// <summary>
/// InfluxDB storage implementation with batch writing and health monitoring
/// </summary>
public class InfluxDbStorage : IMetricsStorage, IDisposable
{
    private readonly InfluxDBClient _client;
    private readonly string _bucket;
    private readonly string _organization;
    private readonly WriteApiAsync _writeApi;
    private readonly ConcurrentQueue<PointData> _writeBuffer;
    private readonly Timer _flushTimer;
    private readonly int _maxBufferSize;
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
    private readonly ILogger<InfluxDbStorage> _logger;
    private bool _disposed;

    public InfluxDbStorage(
        string url,
        string token,
        string bucket,
        string organization,
        ILogger<InfluxDbStorage> logger,
        int batchFlushIntervalSeconds = 5,
        int maxBufferSize = 1000)
    {
        _bucket = bucket;
        _organization = organization;
        _maxBufferSize = maxBufferSize;
        _logger = logger;
        _writeBuffer = new ConcurrentQueue<PointData>();

        // Configure InfluxDB client options to reduce logging
        var options = new InfluxDBClientOptions.Builder()
            .Url(url)
            .AuthenticateToken(token)
            .LogLevel(InfluxDB.Client.Core.LogLevel.None)
            .Build();

        _client = new InfluxDBClient(options);
        _writeApi = _client.GetWriteApiAsync();

        // Start flush timer for batching writes
        if (batchFlushIntervalSeconds > 0)
        {
            _flushTimer = new Timer(
                _ => FlushBufferAsync().GetAwaiter().GetResult(),
                null,
                TimeSpan.FromSeconds(batchFlushIntervalSeconds),
                TimeSpan.FromSeconds(batchFlushIntervalSeconds)
            );
            _logger.LogInformation(
                "InfluxDB batch writing enabled: flush every {FlushInterval}s or {MaxBuffer} points",
                batchFlushIntervalSeconds,
                maxBufferSize);
        }
        else
        {
            // No batching - direct writes
            _flushTimer = null!;
            _logger.LogInformation("InfluxDB batch writing disabled - using direct writes");
        }
    }

    /// <summary>
    /// Write generic metrics to InfluxDB
    /// </summary>
    public async Task WriteMetricsAsync(
        string deviceId,
        string measurementType,
        Dictionary<string, object> metrics,
        Dictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var point = PointData.Measurement(measurementType)
                .Tag("device_id", deviceId)
                .Timestamp(DateTime.UtcNow, WritePrecision.Ns);

            // Add additional tags if provided
            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    point = point.Tag(tag.Key, tag.Value);
                }
            }

            // Add fields
            int fieldsAdded = 0;
            foreach (var metric in metrics)
            {
                point = metric.Value switch
                {
                    int intValue => point.Field(metric.Key, intValue),
                    long longValue => point.Field(metric.Key, longValue),
                    float floatValue => point.Field(metric.Key, floatValue),
                    double doubleValue => point.Field(metric.Key, doubleValue),
                    bool boolValue => point.Field(metric.Key, boolValue),
                    string stringValue => point.Field(metric.Key, stringValue),
                    _ => point.Field(metric.Key, metric.Value.ToString() ?? string.Empty)
                };
                fieldsAdded++;
            }

            if (fieldsAdded == 0)
            {
                _logger.LogWarning(
                    "No fields to write for {MeasurementType} device {DeviceId}",
                    measurementType,
                    deviceId);
                return;
            }

            await WritePointAsync(point, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to write {MeasurementType} metrics for {DeviceId}",
                measurementType,
                deviceId);
            throw;
        }
    }

    /// <summary>
    /// Write interface-specific metrics to InfluxDB
    /// </summary>
    public async Task WriteInterfaceMetricsAsync(
        string deviceId,
        string interfaceId,
        Dictionary<string, object> metrics,
        Dictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var point = PointData.Measurement("interface_metrics")
                .Tag("device_id", deviceId)
                .Tag("interface_id", interfaceId)
                .Timestamp(DateTime.UtcNow, WritePrecision.Ns);

            // Add additional tags if provided
            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    point = point.Tag(tag.Key, tag.Value);
                }
            }

            // Add fields
            int fieldsAdded = 0;
            foreach (var metric in metrics)
            {
                point = metric.Value switch
                {
                    int intValue => point.Field(metric.Key, intValue),
                    long longValue => point.Field(metric.Key, longValue),
                    float floatValue => point.Field(metric.Key, floatValue),
                    double doubleValue => point.Field(metric.Key, doubleValue),
                    bool boolValue => point.Field(metric.Key, boolValue),
                    string stringValue => point.Field(metric.Key, stringValue),
                    _ => point.Field(metric.Key, metric.Value.ToString() ?? string.Empty)
                };
                fieldsAdded++;
            }

            if (fieldsAdded == 0)
            {
                _logger.LogWarning(
                    "No fields to write for interface {InterfaceId} on device {DeviceId}",
                    interfaceId,
                    deviceId);
                return;
            }

            await WritePointAsync(point, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to write interface metrics for interface {InterfaceId} on device {DeviceId}",
                interfaceId,
                deviceId);
            throw;
        }
    }

    /// <summary>
    /// Write SQM (Smart Queue Management) metrics to InfluxDB
    /// </summary>
    public async Task WriteSqmMetricsAsync(
        string deviceId,
        Dictionary<string, object> metrics,
        Dictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var point = PointData.Measurement("sqm_metrics")
                .Tag("device_id", deviceId)
                .Timestamp(DateTime.UtcNow, WritePrecision.Ns);

            // Add additional tags if provided
            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    point = point.Tag(tag.Key, tag.Value);
                }
            }

            // Add fields
            int fieldsAdded = 0;
            foreach (var metric in metrics)
            {
                point = metric.Value switch
                {
                    int intValue => point.Field(metric.Key, intValue),
                    long longValue => point.Field(metric.Key, longValue),
                    float floatValue => point.Field(metric.Key, floatValue),
                    double doubleValue => point.Field(metric.Key, doubleValue),
                    bool boolValue => point.Field(metric.Key, boolValue),
                    string stringValue => point.Field(metric.Key, stringValue),
                    _ => point.Field(metric.Key, metric.Value.ToString() ?? string.Empty)
                };
                fieldsAdded++;
            }

            if (fieldsAdded == 0)
            {
                _logger.LogWarning("No SQM fields to write for device {DeviceId}", deviceId);
                return;
            }

            await WritePointAsync(point, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write SQM metrics for device {DeviceId}", deviceId);
            throw;
        }
    }

    /// <summary>
    /// Check InfluxDB health status
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ping = await _client.PingAsync();
            return ping;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InfluxDB health check failed");
            return false;
        }
    }

    /// <summary>
    /// Write a point to InfluxDB (buffered or direct)
    /// </summary>
    private async Task WritePointAsync(PointData point, CancellationToken cancellationToken)
    {
        // If batching is enabled, add to buffer; otherwise write directly
        if (_flushTimer != null)
        {
            _writeBuffer.Enqueue(point);

            // Flush immediately if buffer is full
            if (_writeBuffer.Count >= _maxBufferSize)
            {
                await FlushBufferAsync();
            }
        }
        else
        {
            // Direct write without batching
            await _writeApi.WritePointAsync(point, _bucket, _organization, cancellationToken);
        }
    }

    /// <summary>
    /// Flush buffered points to InfluxDB
    /// </summary>
    private async Task FlushBufferAsync()
    {
        if (_writeBuffer.IsEmpty)
        {
            return;
        }

        // Prevent concurrent flushes with async-safe semaphore
        if (!await _flushSemaphore.WaitAsync(0))
        {
            return; // Another flush is in progress, skip
        }

        try
        {
            var pointsToWrite = new List<PointData>();

            // Drain the queue
            while (_writeBuffer.TryDequeue(out var point))
            {
                pointsToWrite.Add(point);
            }

            if (pointsToWrite.Count > 0)
            {
                await _writeApi.WritePointsAsync(pointsToWrite, _bucket, _organization);
                _logger.LogDebug("Flushed {Count} points to InfluxDB", pointsToWrite.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush buffer to InfluxDB");
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }

    /// <summary>
    /// Force flush all buffered data
    /// </summary>
    public async Task ForceFlushAsync()
    {
        if (_flushTimer != null)
        {
            await FlushBufferAsync();
        }
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Flush any remaining buffered data
        if (_flushTimer != null && !_writeBuffer.IsEmpty)
        {
            _logger.LogInformation("Flushing remaining {Count} buffered points before disposal...", _writeBuffer.Count);
            FlushBufferAsync().GetAwaiter().GetResult();
        }

        _flushTimer?.Dispose();
        _flushSemaphore?.Dispose();
        _client?.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }
}
