namespace NetworkOptimizer.Monitoring.Models;

/// <summary>
/// Defines a threshold configuration for generating alerts
/// </summary>
public class AlertThreshold
{
    /// <summary>
    /// Unique identifier for the threshold
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Name of the threshold rule
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this threshold monitors
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Whether this threshold is enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Type of metric to monitor (e.g., "cpu", "memory", "interface")
    /// </summary>
    public string MetricType { get; set; } = string.Empty;

    /// <summary>
    /// Specific metric name (e.g., "CpuUsage", "MemoryUsage", "InErrors")
    /// </summary>
    public string MetricName { get; set; } = string.Empty;

    /// <summary>
    /// Threshold value
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// Comparison operator
    /// </summary>
    public ThresholdComparison Comparison { get; set; } = ThresholdComparison.GreaterThan;

    /// <summary>
    /// Alert severity when threshold is exceeded
    /// </summary>
    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;

    /// <summary>
    /// How long the condition must persist before triggering (in seconds)
    /// </summary>
    public int DurationSeconds { get; set; } = 60;

    /// <summary>
    /// Minimum interval between alerts for the same condition (in seconds)
    /// </summary>
    public int CooldownSeconds { get; set; } = 300;

    /// <summary>
    /// Device IP addresses to apply this threshold to (empty = all devices)
    /// </summary>
    public List<string> TargetDevices { get; set; } = new();

    /// <summary>
    /// Device types to apply this threshold to (empty = all types)
    /// </summary>
    public List<DeviceType> TargetDeviceTypes { get; set; } = new();

    /// <summary>
    /// Interface descriptions to monitor (for interface metrics, empty = all)
    /// </summary>
    public List<string> TargetInterfaces { get; set; } = new();

    /// <summary>
    /// Tags for categorization
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Time windows when this threshold is active (empty = always active)
    /// </summary>
    public List<TimeWindow> ActiveWindows { get; set; } = new();

    /// <summary>
    /// Additional configuration options
    /// </summary>
    public Dictionary<string, object> Options { get; set; } = new();

    /// <summary>
    /// When this threshold was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this threshold was last modified
    /// </summary>
    public DateTime? ModifiedAt { get; set; }

    /// <summary>
    /// Check if this threshold applies to a specific device
    /// </summary>
    public bool AppliesTo(DeviceMetrics device)
    {
        // If no target devices specified, applies to all
        if (TargetDevices.Count == 0 && TargetDeviceTypes.Count == 0)
            return true;

        // Check device IP
        if (TargetDevices.Count > 0 && !TargetDevices.Contains(device.IpAddress))
            return false;

        // Check device type
        if (TargetDeviceTypes.Count > 0 && !TargetDeviceTypes.Contains(device.DeviceType))
            return false;

        return true;
    }

    /// <summary>
    /// Check if this threshold applies to a specific interface
    /// </summary>
    public bool AppliesTo(InterfaceMetrics interfaceMetrics)
    {
        // If no target interfaces specified, applies to all
        if (TargetInterfaces.Count == 0)
            return true;

        // Check interface description
        return TargetInterfaces.Any(target =>
            interfaceMetrics.Description.Contains(target, StringComparison.OrdinalIgnoreCase) ||
            interfaceMetrics.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Check if the threshold is currently active based on time windows
    /// </summary>
    public bool IsActiveNow()
    {
        if (!IsEnabled)
            return false;

        // If no time windows defined, always active
        if (ActiveWindows.Count == 0)
            return true;

        var now = DateTime.UtcNow;
        return ActiveWindows.Any(window => window.IsActive(now));
    }

    /// <summary>
    /// Evaluate if a metric value exceeds this threshold
    /// </summary>
    public bool IsExceeded(double value)
    {
        return Comparison switch
        {
            ThresholdComparison.GreaterThan => value > Value,
            ThresholdComparison.GreaterThanOrEqual => value >= Value,
            ThresholdComparison.LessThan => value < Value,
            ThresholdComparison.LessThanOrEqual => value <= Value,
            ThresholdComparison.Equal => Math.Abs(value - Value) < 0.001,
            ThresholdComparison.NotEqual => Math.Abs(value - Value) >= 0.001,
            _ => false
        };
    }
}

/// <summary>
/// Defines a time window when a threshold is active
/// </summary>
public class TimeWindow
{
    /// <summary>
    /// Days of week when this window is active (1=Monday, 7=Sunday)
    /// </summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();

    /// <summary>
    /// Start time (UTC)
    /// </summary>
    public TimeOnly StartTime { get; set; }

    /// <summary>
    /// End time (UTC)
    /// </summary>
    public TimeOnly EndTime { get; set; }

    /// <summary>
    /// Check if this window is currently active
    /// </summary>
    public bool IsActive(DateTime utcNow)
    {
        // Check day of week
        if (DaysOfWeek.Count > 0 && !DaysOfWeek.Contains(utcNow.DayOfWeek))
            return false;

        var currentTime = TimeOnly.FromDateTime(utcNow);

        // Handle time window that spans midnight
        if (EndTime < StartTime)
        {
            return currentTime >= StartTime || currentTime <= EndTime;
        }
        else
        {
            return currentTime >= StartTime && currentTime <= EndTime;
        }
    }
}
