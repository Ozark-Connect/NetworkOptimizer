using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Key-value storage for system-wide settings
/// </summary>
public class SystemSetting
{
    [Key]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Value { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Well-known setting keys
/// </summary>
public static class SystemSettingKeys
{
    public const string Iperf3ParallelStreams = "iperf3.parallel_streams";
    public const string Iperf3Duration = "iperf3.duration_seconds";
    public const string Iperf3Port = "iperf3.port";
}
