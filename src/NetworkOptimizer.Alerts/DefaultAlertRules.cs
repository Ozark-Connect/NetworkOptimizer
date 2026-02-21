using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts;

/// <summary>
/// Default alert rules seeded when the AlertRules table is empty on startup.
/// </summary>
public static class DefaultAlertRules
{
    public static List<AlertRule> GetDefaults() =>
    [
        new AlertRule
        {
            Name = "Audit Score Drop",
            IsEnabled = true,
            EventTypePattern = "audit.completed",
            Source = "audit",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 3600 // 1 hour
        },
        new AlertRule
        {
            Name = "Critical Audit Finding",
            IsEnabled = true,
            EventTypePattern = "audit.critical_findings",
            Source = "audit",
            MinSeverity = AlertSeverity.Critical,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Device Offline",
            IsEnabled = true,
            EventTypePattern = "device.offline",
            Source = "device",
            MinSeverity = AlertSeverity.Error,
            CooldownSeconds = 300 // 5 minutes
        },
        new AlertRule
        {
            Name = "Speed Test Regression",
            IsEnabled = true,
            EventTypePattern = "speedtest.regression",
            Source = "speedtest",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 3600 // 1 hour
        },
        new AlertRule
        {
            Name = "WAN Speed Degradation",
            IsEnabled = true,
            EventTypePattern = "wan.speed_degradation",
            Source = "wan",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            Name = "WiFi Channel Congestion",
            IsEnabled = true,
            EventTypePattern = "wifi.congestion",
            Source = "wifi",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 3600, // 1 hour
            DigestOnly = true // High frequency, low urgency
        }
    ];
}
