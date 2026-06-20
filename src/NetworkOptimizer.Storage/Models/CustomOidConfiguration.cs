using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Per-device custom SNMP OID polling configuration. Each entry maps a user-specified
/// OID to a named InfluxDB field that is collected alongside the standard metrics.
/// </summary>
public class CustomOidConfiguration
{
    [Key]
    public int Id { get; set; }

    /// <summary>Device MAC address this OID applies to.</summary>
    [Required, MaxLength(50)]
    public string DeviceMac { get; set; } = string.Empty;

    /// <summary>SNMP OID to poll, e.g. "1.3.6.1.4.1.41112.1.6.3.3.0".</summary>
    [Required, MaxLength(200)]
    public string Oid { get; set; } = string.Empty;

    /// <summary>InfluxDB field name to store the value as, e.g. "fan_speed_rpm".</summary>
    [Required, MaxLength(100)]
    public string FieldName { get; set; } = string.Empty;

    /// <summary>Expected value type for parsing the SNMP response.</summary>
    public CustomOidValueType ValueType { get; set; }

    /// <summary>Whether this OID is scalar (device-level) or indexed (per-interface).</summary>
    public CustomOidScope Scope { get; set; }

    /// <summary>Whether this OID is actively polled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional user-friendly description.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum CustomOidValueType
{
    Integer = 0,
    Float = 1,
    String = 2
}

public enum CustomOidScope
{
    DeviceLevel = 0,
    InterfaceLevel = 1
}
