using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts;

/// <summary>
/// Default alert rules seeded when the AlertRules table is empty on startup.
/// Rule names use "Nav Title: Description" format to match the app's menu structure.
/// Rules that need infrastructure configured (speed tests, etc.) are disabled by default
/// as helpful starting points for users to enable after setup.
/// </summary>
public static class DefaultAlertRules
{
    public static List<AlertRule> GetDefaults() =>
    [
        // --- Security Audit rules (enabled - only needs UniFi connection) ---
        new AlertRule
        {
            Name = "Security Audit: Score Drop",
            IsEnabled = true,
            EventTypePattern = "audit.score_dropped",
            Source = "audit",
            MinSeverity = AlertSeverity.Warning,
            ThresholdPercent = 15,
            CooldownSeconds = 3600 // 1 hour
        },
        new AlertRule
        {
            Name = "Security Audit: Completed",
            IsEnabled = false,
            EventTypePattern = "audit.completed",
            Source = "audit",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 3600 // 1 hour
        },
        new AlertRule
        {
            Name = "Security Audit: Critical Finding",
            IsEnabled = true,
            EventTypePattern = "audit.critical_findings",
            Source = "audit",
            MinSeverity = AlertSeverity.Critical,
            CooldownSeconds = 0
        },

        // --- Device monitoring (disabled - can be noisy until user configures which devices matter) ---
        new AlertRule
        {
            Name = "Device Offline",
            IsEnabled = false,
            EventTypePattern = "device.offline",
            Source = "device",
            MinSeverity = AlertSeverity.Error,
            CooldownSeconds = 300 // 5 minutes
        },

        // --- Wi-Fi Optimizer (enabled, digest only - works automatically) ---
        new AlertRule
        {
            Name = "Wi-Fi Optimizer: Channel Congestion",
            IsEnabled = true,
            EventTypePattern = "wifi.congestion",
            Source = "wifi",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 3600, // 1 hour
            DigestOnly = true // High frequency, low urgency
        },

        // --- Threat Intelligence (enabled - works with IPS data) ---
        new AlertRule
        {
            Name = "Threat Intelligence: Critical Event",
            IsEnabled = true,
            EventTypePattern = "threats.ips_event",
            Source = "threats",
            MinSeverity = AlertSeverity.Critical,
            CooldownSeconds = 60 // 1 minute
        },

        // --- Threat Intelligence: Attack Chain (enabled - multi-stage attacks are high signal) ---
        new AlertRule
        {
            Name = "Threat Intelligence: Attack Chain",
            IsEnabled = true,
            EventTypePattern = "threats.attack_chain",
            Source = "threats",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 3600 // 1 hour
        },
        new AlertRule
        {
            Name = "Threat Intelligence: Early-Stage Attack Chain",
            IsEnabled = false,
            EventTypePattern = "threats.attack_chain_attempt",
            Source = "threats",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 3600 // 1 hour
        },
        new AlertRule
        {
            Name = "Threat Intelligence: Attack Pattern",
            IsEnabled = false,
            EventTypePattern = "threats.attack_pattern",
            Source = "threats",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 3600 // 1 hour
        },

        // --- WAN Speed Test (disabled - needs gateway SSH configured) ---
        new AlertRule
        {
            Name = "WAN Speed Test: Degradation",
            IsEnabled = false,
            EventTypePattern = "wan.speed_degradation",
            Source = "wan",
            MinSeverity = AlertSeverity.Warning,
            ThresholdPercent = 40,
            CooldownSeconds = 1800 // 30 minutes
        },

        // --- LAN Speed Test (disabled - needs device SSH configured) ---
        new AlertRule
        {
            Name = "LAN Speed Test: Regression",
            IsEnabled = false,
            EventTypePattern = "speedtest.regression",
            Source = "speedtest",
            MinSeverity = AlertSeverity.Warning,
            ThresholdPercent = 25,
            CooldownSeconds = 3600 // 1 hour
        },

        // --- Schedule (enabled - monitors scheduled task failures) ---
        new AlertRule
        {
            Name = "Scheduled Task Failed",
            IsEnabled = true,
            EventTypePattern = "schedule.task_failed",
            Source = "schedule",
            MinSeverity = AlertSeverity.Error,
            CooldownSeconds = 3600 // 1 hour
        }
    ];
}
