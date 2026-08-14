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

        // --- Device monitoring (enabled - these only fire for changes nobody asked for: the
        // evaluators stay silent while UniFi reports a device upgrading or provisioning) ---
        new AlertRule
        {
            Name = "Device Offline",
            IsEnabled = true,
            EventTypePattern = "device.offline",
            Source = "device",
            MinSeverity = AlertSeverity.Error,
            CooldownSeconds = 1800 // 30 minutes - the evaluator fires once per outage
        },
        new AlertRule
        {
            Name = "Device Recovered",
            IsEnabled = true,
            EventTypePattern = "device.recovered",
            Source = "device",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 60 // 1 minute - recoveries are paired with offline events
        },
        // Reboot reasons are published Info for a restart someone meant (commanded, firmware
        // upgrade) and Warning for one nobody did (power loss, hang, panic, watchdog). Seeded at
        // Warning so only the latter notify; drop it to Info to be told about every restart.
        new AlertRule
        {
            Name = "Device Restarted",
            IsEnabled = true,
            EventTypePattern = "device.rebooted",
            Source = "device",
            MinSeverity = AlertSeverity.Warning,
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
        new AlertRule
        {
            Name = "Monitoring: WAN Outage",
            IsEnabled = true,
            EventTypePattern = "monitoring.wan_outage",
            Source = "monitoring",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 600 // 10 minutes - the WAN outage evaluator opens one alert per outage
        },
        new AlertRule
        {
            // Info on purpose: a partial outage on a non-primary WAN publishes at Info, and this
            // rule has to match it.
            Name = "Monitoring: WAN Partial Outage",
            IsEnabled = true,
            EventTypePattern = "monitoring.wan_outage_partial",
            Source = "monitoring",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 600 // 10 minutes - the WAN outage evaluator opens one alert per outage
        },
        new AlertRule
        {
            Name = "Monitoring: WAN Recovered",
            IsEnabled = true,
            EventTypePattern = "monitoring.wan_recovered",
            Source = "monitoring",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 60 // 1 minute - recoveries are paired with outage events
        },

        // --- SFP / PON threshold alerts (enabled - auto-managed for detected modules) ---
        new AlertRule
        {
            Name = "SFP: RX Power Low",
            IsEnabled = true,
            EventTypePattern = "monitoring.sfp_rx_power",
            Source = "monitoring",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            Name = "SFP: TX Power High",
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
        new AlertRule
        {
            // Uncorrected bit errors; always-on signal, and the primary error alert on a
            // link running with payload FEC disabled. Only ONTs whose provider reports BIP trip it.
            Name = "ONT: BIP Error Spike",
            IsEnabled = false,
            EventTypePattern = "ont.bip_errors",
            Source = "ont",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            // Uncorrectable framing-header errors; the live codeword-error signal when payload
            // FEC is disabled. Only augmented SFP-ONT polling reports HEC, so it stays inert otherwise.
            Name = "ONT: HEC Error Spike",
            IsEnabled = false,
            EventTypePattern = "ont.hec_errors",
            Source = "ont",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 1800 // 30 minutes
        },
        new AlertRule
        {
            // Only ONTs whose provider reports a temperature can trip this; the rest never fire it.
            Name = "ONT: Temperature High",
            IsEnabled = false,
            EventTypePattern = "ont.high_temperature",
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
        },

        // --- Starlink dish (disabled until user configures a dish) ---
        // Severity here does NOT follow the per-WAN outage table, which rates a backup's troubles
        // lower. Starlink is usually the backup, and a backup that nothing else monitors is
        // discovered broken at the moment it is needed - so its problems keep real severity.
        //
        // EVERY rule in this block carries NO cooldown, which is deliberate and load-bearing.
        // Two reasons:
        //
        //  1. It would silently drop the alert that replaces a superseded one. These types keep
        //     one open alert per (dish, condition) by having a re-publish supersede its own
        //     predecessor, and AlertProcessingService resolves the old row BEFORE rules are
        //     consulted. Cooldown keys are per (site, rule, device), so a replacement shares the
        //     key of the alert it just closed: an obstruction escalating Warning -> Critical
        //     inside the cooldown would resolve the Warning and then have the Critical suppressed,
        //     leaving a critically obstructed dish with no open alert at all. The WAN outage family
        //     is immune only because a total supersedes a PARTIAL - a different rule, a different
        //     key.
        //  2. It is redundant anyway. StarlinkAlertEvaluator publishes on state changes only, and
        //     is where the real throttling lives: sustain windows and hysteresis on the gated
        //     conditions, "new evidence only" on the dish's own codes, and edge-triggering on
        //     restriction. Nothing here can produce a stream to damp.
        new AlertRule
        {
            // The dish's own verdict on itself: its alert codes, a self-test that started failing,
            // and being taken out of service. Warning, or Critical when it publishes as disabled.
            Name = "Starlink: Dish Fault",
            IsEnabled = false,
            EventTypePattern = "starlink.dish_alert",
            Source = "starlink",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 0 // republishes only when a new code appears or it goes out of service
        },
        new AlertRule
        {
            Name = "Starlink: Obstructed",
            IsEnabled = false,
            EventTypePattern = "starlink.obstructed",
            Source = "starlink",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 0 // 15 minute sustain to open, and at most one escalation per episode
        },
        new AlertRule
        {
            Name = "Starlink: Alignment Drift",
            IsEnabled = false,
            EventTypePattern = "starlink.alignment_drift",
            Source = "starlink",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 0 // opens once per episode; it cannot raise again without recovering first
        },
        new AlertRule
        {
            Name = "Starlink: Ethernet Speed Degraded",
            IsEnabled = false,
            EventTypePattern = "starlink.eth_speed_degraded",
            Source = "starlink",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 0 // 5 minute sustain to open, then once per episode
        },
        new AlertRule
        {
            Name = "Starlink: Repeated Outages",
            IsEnabled = false,
            EventTypePattern = "starlink.outage_burst",
            Source = "starlink",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 0 // opens once when the rolling day crosses the bar, closes when it drops back
        },
        new AlertRule
        {
            // Info on purpose: crossing into a rate limit is a change worth a quiet note, not a
            // fault, and this rule has to match the Info the evaluator publishes.
            Name = "Starlink: Service Rate Limited",
            IsEnabled = false,
            EventTypePattern = "starlink.service_restricted",
            Source = "starlink",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 0 // edge-triggered; a permanently restricted dish never publishes at all
        },
        new AlertRule
        {
            // Unlike every other recovery rule, this one type closes SIX different conditions and
            // they all share the dish's device id - so a cooldown here would swallow the second
            // condition's recovery whenever two clear together.
            Name = "Starlink: Recovered",
            IsEnabled = false,
            EventTypePattern = "starlink.recovered",
            Source = "starlink",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 0
        },

        // --- Firmware Rollout ---
        // Every one of these is published on a transition the executor makes at most once per
        // device or per rollout, so none of them needs a cooldown to keep the volume down. The
        // quiet ones (upcoming, started, completed) are enabled because a rollout running
        // unattended overnight is exactly the thing people want told about.
        new AlertRule
        {
            Name = "Firmware Rollout: Upcoming",
            IsEnabled = true,
            EventTypePattern = "rollout.upcoming",
            Source = "rollout",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Firmware Rollout: Started",
            IsEnabled = true,
            EventTypePattern = "rollout.started",
            Source = "rollout",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Firmware Rollout: Wave Awaiting Approval",
            IsEnabled = true,
            EventTypePattern = "rollout.wave_awaiting_approval",
            Source = "rollout",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Firmware Rollout: Device Stuck Offline",
            IsEnabled = true,
            EventTypePattern = "rollout.device_stuck_offline",
            Source = "rollout",
            MinSeverity = AlertSeverity.Critical,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Firmware Rollout: Model Dropped",
            IsEnabled = true,
            EventTypePattern = "rollout.sku_aborted",
            Source = "rollout",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            // The rollout carries on past this one - the device upgrades are unaffected - so it is
            // a Warning about the console, not a failure of the run.
            Name = "Firmware Rollout: Network Application Update Stuck",
            IsEnabled = true,
            EventTypePattern = "rollout.network_app_update_stuck",
            Source = "rollout",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Firmware Rollout: Heavier After Upgrade",
            IsEnabled = true,
            EventTypePattern = "rollout.resource_regression",
            Source = "rollout",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Firmware Rollout: Lighter After Upgrade",
            IsEnabled = false,
            EventTypePattern = "rollout.resource_improvement",
            Source = "rollout",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Firmware Rollout: Complete",
            IsEnabled = true,
            EventTypePattern = "rollout.completed",
            Source = "rollout",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Firmware Rollout: Report Ready",
            IsEnabled = true,
            EventTypePattern = "rollout.report_ready",
            Source = "rollout",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Firmware Rollout: Postponed",
            IsEnabled = true,
            EventTypePattern = "rollout.postponed_health",
            Source = "rollout",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Firmware Rollout: Rolled Back",
            IsEnabled = true,
            EventTypePattern = "rollout.rollback_executed",
            Source = "rollout",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            // A rollout that cannot see the site stops counting time against its devices, so this
            // is the only thing that says a run is stalled rather than quietly waiting.
            Name = "Firmware Rollout: Site Not Visible",
            IsEnabled = true,
            EventTypePattern = "rollout.visibility_lost",
            Source = "rollout",
            MinSeverity = AlertSeverity.Warning,
            CooldownSeconds = 0
        },
        new AlertRule
        {
            Name = "Firmware Rollout: Site Visible Again",
            IsEnabled = true,
            EventTypePattern = "rollout.visibility_restored",
            Source = "rollout",
            MinSeverity = AlertSeverity.Info,
            CooldownSeconds = 0
        }
    ];
}
