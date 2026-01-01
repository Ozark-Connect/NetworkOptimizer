using System.Net;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Core.Models;

/// <summary>
/// Represents a UniFi network device with its configuration and status information.
/// </summary>
public class UniFiDevice
{
    /// <summary>
    /// Unique identifier for the device in the UniFi controller.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name of the device.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// MAC address of the device.
    /// </summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>
    /// IP address of the device.
    /// </summary>
    public IPAddress? IpAddress { get; set; }

    /// <summary>
    /// Device model identifier.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Type of the device (Gateway, Switch, AccessPoint, etc.).
    /// </summary>
    public DeviceType Type { get; set; } = DeviceType.Unknown;

    /// <summary>
    /// Current firmware version running on the device.
    /// </summary>
    public string FirmwareVersion { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether the device is currently online and reachable.
    /// </summary>
    public bool IsOnline { get; set; }

    /// <summary>
    /// Indicates whether the device is in adoption mode.
    /// </summary>
    public bool IsAdopted { get; set; }

    /// <summary>
    /// Timestamp of when the device was last seen by the controller.
    /// </summary>
    public DateTime LastSeen { get; set; }

    /// <summary>
    /// Device uptime in seconds.
    /// </summary>
    public long UptimeSeconds { get; set; }

    /// <summary>
    /// CPU utilization percentage (0-100).
    /// </summary>
    public double CpuUsage { get; set; }

    /// <summary>
    /// Memory utilization percentage (0-100).
    /// </summary>
    public double MemoryUsage { get; set; }

    /// <summary>
    /// Device temperature in Celsius (if supported).
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    /// Site identifier in the UniFi controller.
    /// </summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>
    /// Site name in the UniFi controller.
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// Additional custom properties and metrics for the device.
    /// </summary>
    public Dictionary<string, object> CustomProperties { get; set; } = new();

    /// <summary>
    /// Number of ports available on the device (for switches).
    /// </summary>
    public int? PortCount { get; set; }

    /// <summary>
    /// List of connected clients (for access points and gateways).
    /// </summary>
    public int ConnectedClientCount { get; set; }

    /// <summary>
    /// Gets the device uptime as a TimeSpan.
    /// </summary>
    public TimeSpan Uptime => TimeSpan.FromSeconds(UptimeSeconds);

    /// <summary>
    /// Determines if the device requires a firmware update.
    /// </summary>
    public bool RequiresFirmwareUpdate { get; set; }

    /// <summary>
    /// Available firmware version (if an update is available).
    /// </summary>
    public string? AvailableFirmwareVersion { get; set; }
}
