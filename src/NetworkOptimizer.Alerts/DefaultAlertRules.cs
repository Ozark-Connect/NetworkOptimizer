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
        },

        // --- WAN Data Usage (disabled - needs data usage tracking configured) ---
        new AlertRule
        {
            Name = "WAN Data Usage: Warning",
            IsEnabled = false,
            EventTypePattern = "wan.data_usage_warning",
            Source = "wan",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 86400 // 24 hours
        },
        new AlertRule
        {
            Name = "WAN Data Usage: Cap Exceeded",
            IsEnabled = false,
            EventTypePattern = "wan.data_usage_exceeded",
            Source = "wan",
            MinSeverity = AlertSeverity.Error,
            CooldownSeconds = 86400 // 24 hours
        },

        // --- UniFi Console (enabled by default - a dead console connection silently blanks most features) ---
        new AlertRule
        {
            Name = "UniFi Console: Connection Failed",
            IsEnabled = true,
            EventTypePattern = "console.connection_failed",
            Source = "console",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes - the connection service fires once per outage
        },
        new AlertRule
        {
            Name = "UniFi Console: Connection Restored",
            IsEnabled = true,
            EventTypePattern = "console.connection_restored",
            Source = "console",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 60 // 1 minute - restores are paired with failure events
        },

        // --- On-Site Agent (enabled by default - an offline agent takes its whole site dark) ---
        new AlertRule
        {
            Name = "On-Site Agent: Offline",
            IsEnabled = true,
            EventTypePattern = "agent.offline",
            Source = "agent",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes - the monitor fires once per outage
        },
        new AlertRule
        {
            Name = "On-Site Agent: Reconnected",
            IsEnabled = true,
            EventTypePattern = "agent.reconnected",
            Source = "agent",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 60 // 1 minute - reconnects are paired with offline events
        },

        // --- Monitoring (enabled by default - users opted into monitoring by configuring it) ---
        new AlertRule
        {
            Name = "Monitoring: Target Offline",
            IsEnabled = true,
            EventTypePattern = "monitoring.target_offline",
            Source = "monitoring",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 600 // 10 minutes - flapping suppression is in the evaluator
        },
        new AlertRule
        {
            Name = "Monitoring: Target Recovered",
            IsEnabled = true,
            EventTypePattern = "monitoring.target_recovered",
            Source = "monitoring",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 60 // 1 minute - recoveries are paired with offline events
        },
        new AlertRule
        {
            Name = "Monitoring: Sustained Packet Loss",
            IsEnabled = true,
            EventTypePattern = "monitoring.target_sustained_loss",
            Source = "monitoring",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },

        // --- SFP / PON threshold alerts (enabled - auto-managed for detected modules) ---
        new AlertRule
        {
            Name = "SFP: PON RX Power Low",
            IsEnabled = true,
            EventTypePattern = "monitoring.sfp_rx_power",
            Source = "monitoring",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            Name = "SFP: PON TX Power High",
            IsEnabled = true,
            EventTypePattern = "monitoring.sfp_tx_power",
            Source = "monitoring",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            Name = "SFP: Temperature High",
            IsEnabled = true,
            EventTypePattern = "monitoring.sfp_temperature",
            Source = "monitoring",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },

        // --- Gateway health (enabled - always available when monitoring is active) ---
        new AlertRule
        {
            Name = "Gateway: High CPU",
            IsEnabled = true,
            EventTypePattern = "device.gateway_high_cpu",
            Source = "device",
            MinSeverity = AlertSeverity.Warning,
            ThresholdPercent = 70,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            Name = "Gateway: High Memory",
            IsEnabled = true,
            EventTypePattern = "device.gateway_high_memory",
            Source = "device",
            MinSeverity = AlertSeverity.Warning,
            ThresholdPercent = 95,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            // Threshold (Celsius) is configured per device type in Monitoring -> Device Stats.
            Name = "Device: Temperature High",
            IsEnabled = true,
            EventTypePattern = "device.high_temperature",
            Source = "device",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },

        // --- Cable modem (disabled until user configures a cable modem) ---
        new AlertRule
        {
            Name = "Cable Modem: Low SNR",
            IsEnabled = false,
            EventTypePattern = "cable_modem.ds_snr_low",
            Source = "cable_modem",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            Name = "Cable Modem: Uncorrectable Errors",
            IsEnabled = false,
            EventTypePattern = "cable_modem.uncorrectable_errors",
            Source = "cable_modem",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            Name = "Cable Modem: DS Power Out of Range",
            IsEnabled = false,
            EventTypePattern = "cable_modem.ds_power_out_of_range",
            Source = "cable_modem",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            Name = "Cable Modem: US Power High",
            IsEnabled = false,
            EventTypePattern = "cable_modem.us_power_high",
            Source = "cable_modem",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            Name = "Cable Modem: Channel Loss",
            IsEnabled = false,
            EventTypePattern = "cable_modem.channel_loss",
            Source = "cable_modem",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 3600 // 1 hour
        },

        // --- External ONT (disabled until user configures an ONT) ---
        new AlertRule
        {
            Name = "ONT: RX Power Low",
            IsEnabled = false,
            EventTypePattern = "ont.rx_power_low",
            Source = "ont",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            Name = "ONT: PON Link Down",
            IsEnabled = false,
            EventTypePattern = "ont.pon_link_down",
            Source = "ont",
            MinSeverity = AlertSeverity.Error,
            CooldownSeconds = 600 // 10 minutes
        },
        new AlertRule
        {
            Name = "ONT: FEC Error Spike",
            IsEnabled = false,
            EventTypePattern = "ont.fec_errors",
            Source = "ont",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },

        // --- Cellular modem (disabled until user configures a cellular modem) ---
        new AlertRule
        {
            Name = "Cellular: Poor Signal",
            IsEnabled = false,
            EventTypePattern = "cellular.signal_poor",
            Source = "cellular",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            Name = "Cellular: Network Downgrade",
            IsEnabled = false,
            EventTypePattern = "cellular.network_downgrade",
            Source = "cellular",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 3600 // 1 hour
        },
        new AlertRule
        {
            Name = "Cellular: Roaming",
            IsEnabled = false,
            EventTypePattern = "cellular.roaming",
            Source = "cellular",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 3600 // 1 hour
        }
    ];
}
