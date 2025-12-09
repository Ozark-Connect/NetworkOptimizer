namespace NetworkOptimizer.Monitoring.Models;

/// <summary>
/// Represents system-level metrics for a network device
/// </summary>
public class DeviceMetrics
{
    /// <summary>
    /// Device IP address
    /// </summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>
    /// Device hostname (sysName)
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// System description (sysDescr)
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// System location (sysLocation)
    /// </summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>
    /// System contact (sysContact)
    /// </summary>
    public string Contact { get; set; } = string.Empty;

    /// <summary>
    /// System uptime in hundredths of a second (sysUpTime)
    /// </summary>
    public long Uptime { get; set; }

    /// <summary>
    /// System object ID (sysObjectID)
    /// </summary>
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>
    /// Device model (vendor-specific OID)
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Firmware version (vendor-specific OID)
    /// </summary>
    public string FirmwareVersion { get; set; } = string.Empty;

    /// <summary>
    /// Device MAC address (vendor-specific OID)
    /// </summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>
    /// CPU usage percentage (0-100)
    /// </summary>
    public double CpuUsage { get; set; }

    /// <summary>
    /// Memory usage percentage (0-100)
    /// </summary>
    public double MemoryUsage { get; set; }

    /// <summary>
    /// Total memory in bytes
    /// </summary>
    public long TotalMemory { get; set; }

    /// <summary>
    /// Used memory in bytes
    /// </summary>
    public long UsedMemory { get; set; }

    /// <summary>
    /// Free memory in bytes
    /// </summary>
    public long FreeMemory { get; set; }

    /// <summary>
    /// Device temperature in Celsius (if available)
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    /// Number of network interfaces
    /// </summary>
    public int InterfaceCount { get; set; }

    /// <summary>
    /// Collection of interface metrics
    /// </summary>
    public List<InterfaceMetrics> Interfaces { get; set; } = new();

    /// <summary>
    /// Timestamp when metrics were collected
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Device type (UniFi specific)
    /// </summary>
    public DeviceType DeviceType { get; set; } = DeviceType.Unknown;

    /// <summary>
    /// Whether the device is currently reachable
    /// </summary>
    public bool IsReachable { get; set; } = true;

    /// <summary>
    /// Any error messages encountered during collection
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Uptime as a TimeSpan
    /// </summary>
    public TimeSpan UptimeSpan => TimeSpan.FromMilliseconds(Uptime * 10);

    /// <summary>
    /// Uptime in days
    /// </summary>
    public double UptimeDays => UptimeSpan.TotalDays;

    /// <summary>
    /// Memory usage in MB
    /// </summary>
    public double UsedMemoryMB => UsedMemory / 1024.0 / 1024.0;

    /// <summary>
    /// Total memory in MB
    /// </summary>
    public double TotalMemoryMB => TotalMemory / 1024.0 / 1024.0;

    /// <summary>
    /// Free memory in MB
    /// </summary>
    public double FreeMemoryMB => FreeMemory / 1024.0 / 1024.0;
}

/// <summary>
/// Type of network device
/// </summary>
public enum DeviceType
{
    Unknown,
    Gateway,
    Switch,
    AccessPoint,
    Router,
    Firewall,
    Server,
    Other
}
