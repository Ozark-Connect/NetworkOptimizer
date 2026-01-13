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
    public const string Iperf3Duration = "iperf3.duration_seconds";
    public const string Iperf3Port = "iperf3.port";

    // Per-device-type parallel stream settings
    public const string Iperf3GatewayParallelStreams = "iperf3.gateway_parallel_streams";
    public const string Iperf3UniFiParallelStreams = "iperf3.unifi_parallel_streams";
    public const string Iperf3OtherParallelStreams = "iperf3.other_parallel_streams";

    // Local iperf3 availability (server-side)
    public const string Iperf3LocalAvailable = "iperf3.local_available";
    public const string Iperf3LocalVersion = "iperf3.local_version";
    public const string Iperf3LocalLastChecked = "iperf3.local_last_checked";

    // UI preferences
    public const string SponsorshipBannerDismissed = "ui.sponsorship_banner_dismissed";
}
