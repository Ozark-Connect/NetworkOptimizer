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

    [MaxLength(2000)]
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

    // Multi-site management
    public const string MultiSiteEnabled = "multisite.enabled";

    // Licensing: stable anonymous id this installation sends with license checks
    // (created on first activation), and the license server base URL override
    // (default https://licensing.ozarkconnect.net; editable for testing).
    public const string LicensingInstallationId = "licensing.installation_id";
    public const string LicensingServerUrl = "licensing.server_url";

    // Internal bookkeeping: JSON map of site slug -> UTC timestamp when the site
    // lost license coverage, anchoring the uncovered-site grace countdown.
    public const string LicensingUncoveredSince = "licensing.uncovered_since";

    // Per-site Client Speed Test target override (agent sites only): an IP/host or a
    // full URL the browser should hit for the LAN speed test, used when the agent's
    // auto-detected LAN IP isn't its client-facing address (e.g. behind a reverse
    // proxy). Client-facing only - the path trace always uses the agent's real LAN IP.
    public const string ClientSpeedTestTargetOverride = "clientspeedtest.target_override";

    // UI preferences (legacy - no longer used)
    public const string SponsorshipBannerDismissed = "ui.sponsorship_banner_dismissed";

    // Sponsorship nag system - progressive tiered display
    public const string SponsorshipLastShownLevel = "ui.sponsorship_last_shown_level";
    public const string SponsorshipLastNagTime = "ui.sponsorship_last_nag_time";
    public const string SponsorshipAlreadySponsor = "ui.sponsorship_already_sponsor";

    // PWA install banner
    public const string PwaBannerDismissed = "ui.pwa_banner_dismissed";

    // Channel recommendation disclaimer
    public const string ChannelDisclaimerDismissed = "ui.channel_disclaimer_dismissed";

    // Dashboard layout preferences
    public const string DashboardLayout = "ui.dashboard_layout";

    // Monitoring Live View map order ("3d-first" or "2d-first")
    public const string MonitoringLiveMapOrder = "ui.monitoring_live_map_order";

    // "results ready" banner for the scheduled upstream re-discovery. Set to "true"
    // when the user dismisses it; auto-cleared once the underlying review flag clears
    // (user committed), so the next scheduled run's results re-arm the banner.
    public const string UpstreamDiscoveryResultsReadyDismissed = "ui.upstream_discovery_results_ready_dismissed";

    // Per-WAN consecutive-miss counters for upstream re-discovery removed-detection. The full
    // key is this prefix + WanInterface; the value is a JSON map of ASN identity -> miss count.
    public const string UpstreamAbsentAsnCountsPrefix = "upstream.absent_asn_counts.";

    // Per-WAN "seen but declined" access-ISP change memory. The full key is this prefix +
    // WanInterface; the value is the new access ASN the user said was NOT a provider switch,
    // so the same detected change doesn't re-prompt on every discovery run. Cleared when a
    // later ISP change is confirmed (fresh baseline).
    public const string UpstreamDeclinedAccessAsnPrefix = "upstream.declined_access_asn.";

    // Map / Satellite settings
    public const string MapboxApiKey = "map.mapbox_token";

    // Channel outcome memory: end of the window the collector last aggregated (UTC, round-trip format)
    public const string ChannelMemoryCollectionWatermark = "wifi.channel_memory_watermark";
}
