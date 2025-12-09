using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring;

/// <summary>
/// Alert state tracking for managing alert lifecycle
/// </summary>
internal class AlertState
{
    public Guid ThresholdId { get; set; }
    public string MetricKey { get; set; } = string.Empty;
    public DateTime FirstTriggered { get; set; }
    public DateTime LastTriggered { get; set; }
    public int TriggerCount { get; set; }
    public Alert? ActiveAlert { get; set; }
    public DateTime? LastAlertSent { get; set; }
}

/// <summary>
/// Interface for alert engine
/// </summary>
public interface IAlertEngine
{
    /// <summary>
    /// Add or update an alert threshold
    /// </summary>
    void AddThreshold(AlertThreshold threshold);

    /// <summary>
    /// Remove an alert threshold
    /// </summary>
    void RemoveThreshold(Guid thresholdId);

    /// <summary>
    /// Get all configured thresholds
    /// </summary>
    List<AlertThreshold> GetThresholds();

    /// <summary>
    /// Evaluate device metrics against thresholds
    /// </summary>
    List<Alert> EvaluateDeviceMetrics(DeviceMetrics metrics);

    /// <summary>
    /// Evaluate interface metrics against thresholds
    /// </summary>
    List<Alert> EvaluateInterfaceMetrics(List<InterfaceMetrics> metrics);

    /// <summary>
    /// Get all active alerts
    /// </summary>
    List<Alert> GetActiveAlerts();

    /// <summary>
    /// Get alert history
    /// </summary>
    List<Alert> GetAlertHistory(int maxCount = 100);

    /// <summary>
    /// Acknowledge an alert
    /// </summary>
    void AcknowledgeAlert(Guid alertId, string acknowledgedBy);

    /// <summary>
    /// Resolve an alert
    /// </summary>
    void ResolveAlert(Guid alertId);

    /// <summary>
    /// Clear old alerts from history
    /// </summary>
    void ClearOldAlerts(TimeSpan olderThan);
}

/// <summary>
/// Alert engine for monitoring metrics and generating alerts based on thresholds
/// </summary>
public class AlertEngine : IAlertEngine
{
    private readonly ILogger<AlertEngine> _logger;
    private readonly Dictionary<Guid, AlertThreshold> _thresholds = new();
    private readonly Dictionary<string, AlertState> _alertStates = new();
    private readonly List<Alert> _alertHistory = new();
    private readonly object _lock = new();

    public AlertEngine(ILogger<AlertEngine> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeDefaultThresholds();
    }

    #region Threshold Management

    /// <summary>
    /// Add or update an alert threshold
    /// </summary>
    public void AddThreshold(AlertThreshold threshold)
    {
        if (threshold == null)
            throw new ArgumentNullException(nameof(threshold));

        lock (_lock)
        {
            _thresholds[threshold.Id] = threshold;
            _logger.LogInformation("Added threshold: {Name} ({Id})", threshold.Name, threshold.Id);
        }
    }

    /// <summary>
    /// Remove an alert threshold
    /// </summary>
    public void RemoveThreshold(Guid thresholdId)
    {
        lock (_lock)
        {
            if (_thresholds.Remove(thresholdId))
            {
                _logger.LogInformation("Removed threshold: {Id}", thresholdId);
            }
        }
    }

    /// <summary>
    /// Get all configured thresholds
    /// </summary>
    public List<AlertThreshold> GetThresholds()
    {
        lock (_lock)
        {
            return _thresholds.Values.ToList();
        }
    }

    #endregion

    #region Metric Evaluation

    /// <summary>
    /// Evaluate device metrics against thresholds
    /// </summary>
    public List<Alert> EvaluateDeviceMetrics(DeviceMetrics metrics)
    {
        if (metrics == null)
            throw new ArgumentNullException(nameof(metrics));

        var alerts = new List<Alert>();

        lock (_lock)
        {
            var deviceThresholds = _thresholds.Values
                .Where(t => t.IsEnabled && t.IsActiveNow() && t.AppliesTo(metrics))
                .ToList();

            foreach (var threshold in deviceThresholds)
            {
                try
                {
                    var alert = EvaluateDeviceThreshold(threshold, metrics);
                    if (alert != null)
                    {
                        alerts.Add(alert);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to evaluate threshold {ThresholdName} for device {Device}",
                        threshold.Name, metrics.Hostname);
                }
            }
        }

        return alerts;
    }

    /// <summary>
    /// Evaluate interface metrics against thresholds
    /// </summary>
    public List<Alert> EvaluateInterfaceMetrics(List<InterfaceMetrics> metrics)
    {
        if (metrics == null)
            throw new ArgumentNullException(nameof(metrics));

        var alerts = new List<Alert>();

        lock (_lock)
        {
            var interfaceThresholds = _thresholds.Values
                .Where(t => t.IsEnabled && t.IsActiveNow() && t.MetricType == "interface")
                .ToList();

            foreach (var interfaceMetric in metrics)
            {
                foreach (var threshold in interfaceThresholds)
                {
                    try
                    {
                        if (threshold.AppliesTo(interfaceMetric))
                        {
                            var alert = EvaluateInterfaceThreshold(threshold, interfaceMetric);
                            if (alert != null)
                            {
                                alerts.Add(alert);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to evaluate threshold {ThresholdName} for interface {Interface}",
                            threshold.Name, interfaceMetric.Description);
                    }
                }
            }
        }

        return alerts;
    }

    #endregion

    #region Alert Management

    /// <summary>
    /// Get all active alerts
    /// </summary>
    public List<Alert> GetActiveAlerts()
    {
        lock (_lock)
        {
            return _alertHistory
                .Where(a => a.Status == AlertStatus.Active)
                .OrderByDescending(a => a.TriggeredAt)
                .ToList();
        }
    }

    /// <summary>
    /// Get alert history
    /// </summary>
    public List<Alert> GetAlertHistory(int maxCount = 100)
    {
        lock (_lock)
        {
            return _alertHistory
                .OrderByDescending(a => a.TriggeredAt)
                .Take(maxCount)
                .ToList();
        }
    }

    /// <summary>
    /// Acknowledge an alert
    /// </summary>
    public void AcknowledgeAlert(Guid alertId, string acknowledgedBy)
    {
        lock (_lock)
        {
            var alert = _alertHistory.FirstOrDefault(a => a.Id == alertId);
            if (alert != null)
            {
                alert.IsAcknowledged = true;
                alert.AcknowledgedBy = acknowledgedBy;
                alert.AcknowledgedAt = DateTime.UtcNow;
                alert.Status = AlertStatus.Acknowledged;
                alert.UpdatedAt = DateTime.UtcNow;

                _logger.LogInformation("Alert {AlertId} acknowledged by {User}", alertId, acknowledgedBy);
            }
        }
    }

    /// <summary>
    /// Resolve an alert
    /// </summary>
    public void ResolveAlert(Guid alertId)
    {
        lock (_lock)
        {
            var alert = _alertHistory.FirstOrDefault(a => a.Id == alertId);
            if (alert != null && alert.Status != AlertStatus.Resolved)
            {
                alert.Status = AlertStatus.Resolved;
                alert.ResolvedAt = DateTime.UtcNow;
                alert.UpdatedAt = DateTime.UtcNow;

                _logger.LogInformation("Alert {AlertId} resolved", alertId);
            }
        }
    }

    /// <summary>
    /// Clear old alerts from history
    /// </summary>
    public void ClearOldAlerts(TimeSpan olderThan)
    {
        lock (_lock)
        {
            var cutoffDate = DateTime.UtcNow - olderThan;
            var removedCount = _alertHistory.RemoveAll(a =>
                a.Status == AlertStatus.Resolved &&
                a.ResolvedAt.HasValue &&
                a.ResolvedAt.Value < cutoffDate);

            if (removedCount > 0)
            {
                _logger.LogInformation("Cleared {Count} old alerts", removedCount);
            }
        }
    }

    #endregion

    #region Private Evaluation Methods

    private Alert? EvaluateDeviceThreshold(AlertThreshold threshold, DeviceMetrics metrics)
    {
        double? metricValue = threshold.MetricName switch
        {
            "CpuUsage" => metrics.CpuUsage,
            "MemoryUsage" => metrics.MemoryUsage,
            "Temperature" => metrics.Temperature,
            "Uptime" => metrics.UptimeDays,
            _ => null
        };

        if (!metricValue.HasValue)
            return null;

        return EvaluateThreshold(
            threshold,
            metricValue.Value,
            $"device_{metrics.IpAddress}_{threshold.MetricName}",
            metrics.IpAddress,
            metrics.Hostname,
            null
        );
    }

    private Alert? EvaluateInterfaceThreshold(AlertThreshold threshold, InterfaceMetrics metrics)
    {
        double? metricValue = threshold.MetricName switch
        {
            "InErrors" => metrics.InErrors,
            "OutErrors" => metrics.OutErrors,
            "InDiscards" => metrics.InDiscards,
            "OutDiscards" => metrics.OutDiscards,
            "InOctets" => metrics.InOctets,
            "OutOctets" => metrics.OutOctets,
            "OperStatus" => metrics.OperStatus,
            "AdminStatus" => metrics.AdminStatus,
            _ => null
        };

        if (!metricValue.HasValue)
            return null;

        return EvaluateThreshold(
            threshold,
            metricValue.Value,
            $"interface_{metrics.DeviceIp}_{metrics.Index}_{threshold.MetricName}",
            metrics.DeviceIp,
            metrics.DeviceHostname,
            metrics.Description
        );
    }

    private Alert? EvaluateThreshold(
        AlertThreshold threshold,
        double currentValue,
        string metricKey,
        string deviceIp,
        string deviceHostname,
        string? interfaceDescription)
    {
        var isExceeded = threshold.IsExceeded(currentValue);

        if (!_alertStates.TryGetValue(metricKey, out var state))
        {
            state = new AlertState
            {
                ThresholdId = threshold.Id,
                MetricKey = metricKey
            };
            _alertStates[metricKey] = state;
        }

        var now = DateTime.UtcNow;

        if (isExceeded)
        {
            if (state.FirstTriggered == default)
            {
                state.FirstTriggered = now;
                state.LastTriggered = now;
                state.TriggerCount = 1;
            }
            else
            {
                state.LastTriggered = now;
                state.TriggerCount++;
            }

            // Check if duration requirement is met
            var duration = now - state.FirstTriggered;
            if (duration.TotalSeconds < threshold.DurationSeconds)
            {
                _logger.LogDebug("Threshold {Name} exceeded but duration requirement not met ({Duration}s < {Required}s)",
                    threshold.Name, duration.TotalSeconds, threshold.DurationSeconds);
                return null;
            }

            // Check cooldown period
            if (state.LastAlertSent.HasValue)
            {
                var timeSinceLastAlert = now - state.LastAlertSent.Value;
                if (timeSinceLastAlert.TotalSeconds < threshold.CooldownSeconds)
                {
                    _logger.LogDebug("Threshold {Name} in cooldown period ({Elapsed}s < {Required}s)",
                        threshold.Name, timeSinceLastAlert.TotalSeconds, threshold.CooldownSeconds);
                    return null;
                }
            }

            // Create or update alert
            Alert alert;
            if (state.ActiveAlert == null)
            {
                alert = CreateAlert(threshold, currentValue, deviceIp, deviceHostname, interfaceDescription);
                state.ActiveAlert = alert;
                _alertHistory.Add(alert);
                _logger.LogWarning("New alert triggered: {Title}", alert.Title);
            }
            else
            {
                alert = state.ActiveAlert;
                alert.CurrentValue = currentValue;
                alert.TriggerCount = state.TriggerCount;
                alert.UpdatedAt = now;
            }

            state.LastAlertSent = now;
            return alert;
        }
        else
        {
            // Condition no longer met - resolve alert if active
            if (state.ActiveAlert != null && state.ActiveAlert.Status == AlertStatus.Active)
            {
                state.ActiveAlert.Status = AlertStatus.Resolved;
                state.ActiveAlert.ResolvedAt = now;
                state.ActiveAlert.UpdatedAt = now;
                _logger.LogInformation("Alert resolved: {Title}", state.ActiveAlert.Title);
            }

            // Reset state
            state.FirstTriggered = default;
            state.TriggerCount = 0;
            state.ActiveAlert = null;
        }

        return null;
    }

    private Alert CreateAlert(
        AlertThreshold threshold,
        double currentValue,
        string deviceIp,
        string deviceHostname,
        string? interfaceDescription)
    {
        var alert = new Alert
        {
            Severity = threshold.Severity,
            MetricType = threshold.MetricType,
            MetricName = threshold.MetricName,
            CurrentValue = currentValue,
            ThresholdValue = threshold.Value,
            Comparison = threshold.Comparison,
            DeviceIp = deviceIp,
            DeviceHostname = deviceHostname,
            InterfaceDescription = interfaceDescription,
            Tags = new List<string>(threshold.Tags)
        };

        // Generate title and message
        var comparisonText = threshold.Comparison switch
        {
            ThresholdComparison.GreaterThan => "exceeded",
            ThresholdComparison.GreaterThanOrEqual => "exceeded or equal to",
            ThresholdComparison.LessThan => "below",
            ThresholdComparison.LessThanOrEqual => "below or equal to",
            ThresholdComparison.Equal => "equal to",
            ThresholdComparison.NotEqual => "not equal to",
            _ => "compared to"
        };

        if (string.IsNullOrWhiteSpace(interfaceDescription))
        {
            alert.Title = $"{threshold.Name} - {deviceHostname}";
            alert.Message = $"{threshold.MetricName} {comparisonText} threshold on {deviceHostname} ({deviceIp}). " +
                          $"Current value: {currentValue:F2}, Threshold: {threshold.Value:F2}";
        }
        else
        {
            alert.Title = $"{threshold.Name} - {deviceHostname} ({interfaceDescription})";
            alert.Message = $"{threshold.MetricName} {comparisonText} threshold on interface {interfaceDescription} " +
                          $"of {deviceHostname} ({deviceIp}). " +
                          $"Current value: {currentValue:F2}, Threshold: {threshold.Value:F2}";
        }

        return alert;
    }

    #endregion

    #region Default Thresholds

    private void InitializeDefaultThresholds()
    {
        // High CPU usage
        AddThreshold(new AlertThreshold
        {
            Name = "High CPU Usage",
            Description = "CPU usage exceeds 90%",
            MetricType = "device",
            MetricName = "CpuUsage",
            Value = 90,
            Comparison = ThresholdComparison.GreaterThan,
            Severity = AlertSeverity.Warning,
            DurationSeconds = 300, // 5 minutes
            CooldownSeconds = 900, // 15 minutes
            Tags = new List<string> { "cpu", "performance" }
        });

        // Critical CPU usage
        AddThreshold(new AlertThreshold
        {
            Name = "Critical CPU Usage",
            Description = "CPU usage exceeds 95%",
            MetricType = "device",
            MetricName = "CpuUsage",
            Value = 95,
            Comparison = ThresholdComparison.GreaterThan,
            Severity = AlertSeverity.Critical,
            DurationSeconds = 180, // 3 minutes
            CooldownSeconds = 600, // 10 minutes
            Tags = new List<string> { "cpu", "performance", "critical" }
        });

        // High memory usage
        AddThreshold(new AlertThreshold
        {
            Name = "High Memory Usage",
            Description = "Memory usage exceeds 85%",
            MetricType = "device",
            MetricName = "MemoryUsage",
            Value = 85,
            Comparison = ThresholdComparison.GreaterThan,
            Severity = AlertSeverity.Warning,
            DurationSeconds = 300,
            CooldownSeconds = 900,
            Tags = new List<string> { "memory", "performance" }
        });

        // Critical memory usage
        AddThreshold(new AlertThreshold
        {
            Name = "Critical Memory Usage",
            Description = "Memory usage exceeds 95%",
            MetricType = "device",
            MetricName = "MemoryUsage",
            Value = 95,
            Comparison = ThresholdComparison.GreaterThan,
            Severity = AlertSeverity.Critical,
            DurationSeconds = 180,
            CooldownSeconds = 600,
            Tags = new List<string> { "memory", "performance", "critical" }
        });

        // High temperature
        AddThreshold(new AlertThreshold
        {
            Name = "High Temperature",
            Description = "Device temperature exceeds 75°C",
            MetricType = "device",
            MetricName = "Temperature",
            Value = 75,
            Comparison = ThresholdComparison.GreaterThan,
            Severity = AlertSeverity.Warning,
            DurationSeconds = 300,
            CooldownSeconds = 900,
            Tags = new List<string> { "temperature", "hardware" }
        });

        // Interface errors
        AddThreshold(new AlertThreshold
        {
            Name = "Interface Errors",
            Description = "Interface has errors",
            MetricType = "interface",
            MetricName = "InErrors",
            Value = 100,
            Comparison = ThresholdComparison.GreaterThan,
            Severity = AlertSeverity.Warning,
            DurationSeconds = 300,
            CooldownSeconds = 1800,
            Tags = new List<string> { "interface", "errors" }
        });

        // Interface down
        AddThreshold(new AlertThreshold
        {
            Name = "Interface Down",
            Description = "Interface operational status is down",
            MetricType = "interface",
            MetricName = "OperStatus",
            Value = 1,
            Comparison = ThresholdComparison.LessThan,
            Severity = AlertSeverity.Error,
            DurationSeconds = 60,
            CooldownSeconds = 300,
            Tags = new List<string> { "interface", "status" }
        });

        _logger.LogInformation("Initialized {Count} default alert thresholds", _thresholds.Count);
    }

    #endregion
}
