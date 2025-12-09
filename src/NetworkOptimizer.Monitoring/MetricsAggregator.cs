using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring;

/// <summary>
/// Metric source types
/// </summary>
public enum MetricSource
{
    Snmp,
    Agent,
    UniFiApi,
    Custom
}

/// <summary>
/// Aggregated metric data point
/// </summary>
public class AggregatedMetric
{
    /// <summary>
    /// Unique identifier for the metric
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Normalized metric name (e.g., "device.cpu.usage", "interface.in.octets")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Metric value
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// Source of the metric
    /// </summary>
    public MetricSource Source { get; set; }

    /// <summary>
    /// Timestamp when the metric was collected
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Device IP address
    /// </summary>
    public string DeviceIp { get; set; } = string.Empty;

    /// <summary>
    /// Device hostname
    /// </summary>
    public string DeviceHostname { get; set; } = string.Empty;

    /// <summary>
    /// Interface description (if applicable)
    /// </summary>
    public string? InterfaceDescription { get; set; }

    /// <summary>
    /// Additional tags for categorization
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>
    /// Additional fields
    /// </summary>
    public Dictionary<string, object> Fields { get; set; } = new();
}

/// <summary>
/// Batch of aggregated metrics ready for storage
/// </summary>
public class MetricsBatch
{
    /// <summary>
    /// Batch identifier
    /// </summary>
    public Guid BatchId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// When the batch was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Metrics in this batch
    /// </summary>
    public List<AggregatedMetric> Metrics { get; set; } = new();

    /// <summary>
    /// Number of metrics in the batch
    /// </summary>
    public int Count => Metrics.Count;

    /// <summary>
    /// Whether the batch is ready for storage
    /// </summary>
    public bool IsReady { get; set; }
}

/// <summary>
/// Interface for metrics aggregation
/// </summary>
public interface IMetricsAggregator
{
    /// <summary>
    /// Add device metrics to the aggregator
    /// </summary>
    void AddDeviceMetrics(DeviceMetrics deviceMetrics, MetricSource source = MetricSource.Snmp);

    /// <summary>
    /// Add interface metrics to the aggregator
    /// </summary>
    void AddInterfaceMetrics(List<InterfaceMetrics> interfaceMetrics, MetricSource source = MetricSource.Snmp);

    /// <summary>
    /// Add a custom metric
    /// </summary>
    void AddCustomMetric(string name, double value, string deviceIp, Dictionary<string, string>? tags = null);

    /// <summary>
    /// Get current batch of metrics
    /// </summary>
    MetricsBatch GetBatch();

    /// <summary>
    /// Clear current batch
    /// </summary>
    void ClearBatch();

    /// <summary>
    /// Get metrics count in current batch
    /// </summary>
    int GetBatchCount();
}

/// <summary>
/// Aggregates metrics from multiple sources and normalizes them for storage
/// </summary>
public class MetricsAggregator : IMetricsAggregator
{
    private readonly ILogger<MetricsAggregator> _logger;
    private readonly List<AggregatedMetric> _currentBatch = new();
    private readonly object _batchLock = new();
    private readonly int _maxBatchSize;

    public MetricsAggregator(ILogger<MetricsAggregator> logger, int maxBatchSize = 1000)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxBatchSize = maxBatchSize;
    }

    /// <summary>
    /// Add device metrics to the aggregator
    /// </summary>
    public void AddDeviceMetrics(DeviceMetrics deviceMetrics, MetricSource source = MetricSource.Snmp)
    {
        if (deviceMetrics == null)
            throw new ArgumentNullException(nameof(deviceMetrics));

        try
        {
            var metrics = new List<AggregatedMetric>();
            var baseTags = CreateBaseTags(deviceMetrics);

            // System uptime
            if (deviceMetrics.Uptime > 0)
            {
                metrics.Add(CreateMetric(
                    "device.uptime",
                    deviceMetrics.Uptime,
                    deviceMetrics,
                    source,
                    baseTags
                ));
            }

            // CPU usage
            if (deviceMetrics.CpuUsage > 0)
            {
                metrics.Add(CreateMetric(
                    "device.cpu.usage",
                    deviceMetrics.CpuUsage,
                    deviceMetrics,
                    source,
                    baseTags
                ));
            }

            // Memory metrics
            if (deviceMetrics.MemoryUsage > 0)
            {
                metrics.Add(CreateMetric(
                    "device.memory.usage_percent",
                    deviceMetrics.MemoryUsage,
                    deviceMetrics,
                    source,
                    baseTags
                ));
            }

            if (deviceMetrics.TotalMemory > 0)
            {
                metrics.Add(CreateMetric(
                    "device.memory.total_bytes",
                    deviceMetrics.TotalMemory,
                    deviceMetrics,
                    source,
                    baseTags
                ));

                metrics.Add(CreateMetric(
                    "device.memory.used_bytes",
                    deviceMetrics.UsedMemory,
                    deviceMetrics,
                    source,
                    baseTags
                ));

                metrics.Add(CreateMetric(
                    "device.memory.free_bytes",
                    deviceMetrics.FreeMemory,
                    deviceMetrics,
                    source,
                    baseTags
                ));
            }

            // Temperature
            if (deviceMetrics.Temperature.HasValue)
            {
                metrics.Add(CreateMetric(
                    "device.temperature.celsius",
                    deviceMetrics.Temperature.Value,
                    deviceMetrics,
                    source,
                    baseTags
                ));
            }

            // Interface count
            metrics.Add(CreateMetric(
                "device.interfaces.count",
                deviceMetrics.InterfaceCount,
                deviceMetrics,
                source,
                baseTags
            ));

            // Reachability
            metrics.Add(CreateMetric(
                "device.reachable",
                deviceMetrics.IsReachable ? 1 : 0,
                deviceMetrics,
                source,
                baseTags
            ));

            AddMetricsToBatch(metrics);

            _logger.LogDebug("Added {Count} device metrics for {Device}", metrics.Count, deviceMetrics.Hostname);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add device metrics for {Device}", deviceMetrics.Hostname);
        }
    }

    /// <summary>
    /// Add interface metrics to the aggregator
    /// </summary>
    public void AddInterfaceMetrics(List<InterfaceMetrics> interfaceMetrics, MetricSource source = MetricSource.Snmp)
    {
        if (interfaceMetrics == null)
            throw new ArgumentNullException(nameof(interfaceMetrics));

        try
        {
            var metrics = new List<AggregatedMetric>();

            foreach (var iface in interfaceMetrics)
            {
                var baseTags = CreateInterfaceTags(iface);

                // Interface status
                metrics.Add(CreateInterfaceMetric(
                    "interface.admin_status",
                    iface.AdminStatus,
                    iface,
                    source,
                    baseTags
                ));

                metrics.Add(CreateInterfaceMetric(
                    "interface.oper_status",
                    iface.OperStatus,
                    iface,
                    source,
                    baseTags
                ));

                metrics.Add(CreateInterfaceMetric(
                    "interface.is_up",
                    iface.IsUp ? 1 : 0,
                    iface,
                    source,
                    baseTags
                ));

                // Speed
                if (iface.Speed > 0 || iface.HighSpeed > 0)
                {
                    metrics.Add(CreateInterfaceMetric(
                        "interface.speed_mbps",
                        iface.SpeedMbps,
                        iface,
                        source,
                        baseTags
                    ));
                }

                // Traffic counters
                metrics.Add(CreateInterfaceMetric(
                    "interface.in_octets",
                    iface.InOctets,
                    iface,
                    source,
                    baseTags
                ));

                metrics.Add(CreateInterfaceMetric(
                    "interface.out_octets",
                    iface.OutOctets,
                    iface,
                    source,
                    baseTags
                ));

                // Packet counters
                metrics.Add(CreateInterfaceMetric(
                    "interface.in_packets",
                    iface.TotalInPackets,
                    iface,
                    source,
                    baseTags
                ));

                metrics.Add(CreateInterfaceMetric(
                    "interface.out_packets",
                    iface.TotalOutPackets,
                    iface,
                    source,
                    baseTags
                ));

                // Unicast packets
                if (iface.InUcastPkts > 0 || iface.OutUcastPkts > 0)
                {
                    metrics.Add(CreateInterfaceMetric(
                        "interface.in_ucast_packets",
                        iface.InUcastPkts,
                        iface,
                        source,
                        baseTags
                    ));

                    metrics.Add(CreateInterfaceMetric(
                        "interface.out_ucast_packets",
                        iface.OutUcastPkts,
                        iface,
                        source,
                        baseTags
                    ));
                }

                // Multicast packets
                if (iface.InMulticastPkts > 0 || iface.OutMulticastPkts > 0)
                {
                    metrics.Add(CreateInterfaceMetric(
                        "interface.in_multicast_packets",
                        iface.InMulticastPkts,
                        iface,
                        source,
                        baseTags
                    ));

                    metrics.Add(CreateInterfaceMetric(
                        "interface.out_multicast_packets",
                        iface.OutMulticastPkts,
                        iface,
                        source,
                        baseTags
                    ));
                }

                // Broadcast packets
                if (iface.InBroadcastPkts > 0 || iface.OutBroadcastPkts > 0)
                {
                    metrics.Add(CreateInterfaceMetric(
                        "interface.in_broadcast_packets",
                        iface.InBroadcastPkts,
                        iface,
                        source,
                        baseTags
                    ));

                    metrics.Add(CreateInterfaceMetric(
                        "interface.out_broadcast_packets",
                        iface.OutBroadcastPkts,
                        iface,
                        source,
                        baseTags
                    ));
                }

                // Errors and discards
                metrics.Add(CreateInterfaceMetric(
                    "interface.in_errors",
                    iface.InErrors,
                    iface,
                    source,
                    baseTags
                ));

                metrics.Add(CreateInterfaceMetric(
                    "interface.out_errors",
                    iface.OutErrors,
                    iface,
                    source,
                    baseTags
                ));

                metrics.Add(CreateInterfaceMetric(
                    "interface.in_discards",
                    iface.InDiscards,
                    iface,
                    source,
                    baseTags
                ));

                metrics.Add(CreateInterfaceMetric(
                    "interface.out_discards",
                    iface.OutDiscards,
                    iface,
                    source,
                    baseTags
                ));

                // Unknown protocols
                if (iface.InUnknownProtos > 0)
                {
                    metrics.Add(CreateInterfaceMetric(
                        "interface.in_unknown_protos",
                        iface.InUnknownProtos,
                        iface,
                        source,
                        baseTags
                    ));
                }
            }

            AddMetricsToBatch(metrics);

            _logger.LogDebug("Added {Count} interface metrics for {InterfaceCount} interfaces",
                metrics.Count, interfaceMetrics.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add interface metrics");
        }
    }

    /// <summary>
    /// Add a custom metric
    /// </summary>
    public void AddCustomMetric(string name, double value, string deviceIp, Dictionary<string, string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Metric name cannot be empty", nameof(name));

        if (string.IsNullOrWhiteSpace(deviceIp))
            throw new ArgumentException("Device IP cannot be empty", nameof(deviceIp));

        try
        {
            var metric = new AggregatedMetric
            {
                Name = NormalizeMetricName(name),
                Value = value,
                Source = MetricSource.Custom,
                DeviceIp = deviceIp,
                Tags = tags ?? new Dictionary<string, string>()
            };

            AddMetricsToBatch(new List<AggregatedMetric> { metric });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add custom metric {Name}", name);
        }
    }

    /// <summary>
    /// Get current batch of metrics
    /// </summary>
    public MetricsBatch GetBatch()
    {
        lock (_batchLock)
        {
            return new MetricsBatch
            {
                Metrics = new List<AggregatedMetric>(_currentBatch),
                IsReady = _currentBatch.Count >= _maxBatchSize
            };
        }
    }

    /// <summary>
    /// Clear current batch
    /// </summary>
    public void ClearBatch()
    {
        lock (_batchLock)
        {
            var count = _currentBatch.Count;
            _currentBatch.Clear();
            _logger.LogDebug("Cleared batch of {Count} metrics", count);
        }
    }

    /// <summary>
    /// Get metrics count in current batch
    /// </summary>
    public int GetBatchCount()
    {
        lock (_batchLock)
        {
            return _currentBatch.Count;
        }
    }

    #region Private Helper Methods

    private void AddMetricsToBatch(List<AggregatedMetric> metrics)
    {
        lock (_batchLock)
        {
            _currentBatch.AddRange(metrics);

            if (_currentBatch.Count >= _maxBatchSize)
            {
                _logger.LogInformation("Metrics batch reached max size ({Size}), ready for storage", _maxBatchSize);
            }
        }
    }

    private AggregatedMetric CreateMetric(
        string name,
        double value,
        DeviceMetrics device,
        MetricSource source,
        Dictionary<string, string> tags)
    {
        return new AggregatedMetric
        {
            Name = NormalizeMetricName(name),
            Value = value,
            Source = source,
            Timestamp = device.Timestamp,
            DeviceIp = device.IpAddress,
            DeviceHostname = device.Hostname,
            Tags = tags
        };
    }

    private AggregatedMetric CreateInterfaceMetric(
        string name,
        double value,
        InterfaceMetrics iface,
        MetricSource source,
        Dictionary<string, string> tags)
    {
        return new AggregatedMetric
        {
            Name = NormalizeMetricName(name),
            Value = value,
            Source = source,
            Timestamp = iface.Timestamp,
            DeviceIp = iface.DeviceIp,
            DeviceHostname = iface.DeviceHostname,
            InterfaceDescription = iface.Description,
            Tags = tags
        };
    }

    private Dictionary<string, string> CreateBaseTags(DeviceMetrics device)
    {
        var tags = new Dictionary<string, string>
        {
            { "device_ip", device.IpAddress },
            { "device_type", device.DeviceType.ToString() }
        };

        if (!string.IsNullOrWhiteSpace(device.Hostname))
            tags["hostname"] = device.Hostname;

        if (!string.IsNullOrWhiteSpace(device.Model))
            tags["model"] = device.Model;

        if (!string.IsNullOrWhiteSpace(device.Location))
            tags["location"] = device.Location;

        return tags;
    }

    private Dictionary<string, string> CreateInterfaceTags(InterfaceMetrics iface)
    {
        var tags = new Dictionary<string, string>
        {
            { "device_ip", iface.DeviceIp },
            { "interface_index", iface.Index.ToString() },
            { "interface_description", iface.Description }
        };

        if (!string.IsNullOrWhiteSpace(iface.DeviceHostname))
            tags["hostname"] = iface.DeviceHostname;

        if (!string.IsNullOrWhiteSpace(iface.Name))
            tags["interface_name"] = iface.Name;

        if (!string.IsNullOrWhiteSpace(iface.PhysicalAddress))
            tags["mac_address"] = iface.PhysicalAddress;

        return tags;
    }

    private string NormalizeMetricName(string name)
    {
        // Normalize metric names to lowercase with dots as separators
        return name.ToLowerInvariant()
            .Replace("__", ".")
            .Replace("_", ".")
            .Replace(" ", ".")
            .Replace("-", ".");
    }

    #endregion
}
