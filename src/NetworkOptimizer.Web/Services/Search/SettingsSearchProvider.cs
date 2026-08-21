using Microsoft.AspNetCore.Authorization;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Services.Search;

/// <summary>
/// Indexes the cards on the Settings page. Every entry's <see cref="AppSearchEntry.Anchor"/> is an
/// element id that already exists for the /settings#anchor deep links, so a hit reuses the same
/// scroll-and-highlight jump those links get rather than inventing a second way to arrive.
///
/// Keywords are the words a user would type for something that is INSIDE a card rather than in its
/// title - field labels, provider names, the vocabulary the card's own help text uses. They are the
/// difference between a search box that finds "Cable Modem Monitoring" and one that finds it when
/// you type "docsis". Keep them true to what the card actually holds; a keyword that leads
/// somewhere the setting is not is worse than no keyword at all.
/// </summary>
public sealed class SettingsSearchProvider : IAppSearchProvider
{
    private const string SettingsArea = "Settings";

    private readonly SiteContextService _siteContext;
    private readonly ISiteManagementService _siteManagement;
    private readonly IAuthorizationService _authorization;

    public SettingsSearchProvider(
        SiteContextService siteContext,
        ISiteManagementService siteManagement,
        IAuthorizationService authorization)
    {
        _siteContext = siteContext;
        _siteManagement = siteManagement;
        _authorization = authorization;
    }

    /// <inheritdoc />
    public string Area => SettingsArea;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppSearchEntry>> GetEntriesAsync(AppSearchContext context)
    {
        var isAdmin = context.User is not null
            && (await _authorization.AuthorizeAsync(context.User, Policies.RequireAdmin)).Succeeded;
        var isDefaultSite = _siteContext.IsDefault;
        var multiSiteEnabled = await _siteManagement.IsMultiSiteEnabledAsync();

        return Index.Where(i => CanReach(i.Reach, isAdmin, isDefaultSite, multiSiteEnabled))
            .Select(i => i.Entry)
            .ToList();
    }

    /// <summary>
    /// What a caller has to be for the card to be on their page. These mirror the conditions the
    /// Settings markup uses to draw each tab and card - a result that lands on a card the caller's
    /// Settings page never rendered is worse than no result.
    /// </summary>
    private enum Reach
    {
        /// <summary>Every site, every role that can open Settings at all.</summary>
        Anyone,

        /// <summary>Main site only: the card configures something shared by the whole install.</summary>
        DefaultSite,

        /// <summary>Main site and a global Admin - the Application and Audit Log tabs, and the
        /// instance-wide half of Identity.</summary>
        InstanceWide,

        /// <summary>The Multi-Site tab, offered to an Admin always and to anyone once it is on.</summary>
        MultiSite,

        /// <summary>Inside the Multi-Site tab, and only rendered once multi-site is on.</summary>
        MultiSiteEnabledAdmin,
    }

    private static bool CanReach(Reach reach, bool isAdmin, bool isDefaultSite, bool multiSiteEnabled) => reach switch
    {
        Reach.Anyone => true,
        Reach.DefaultSite => isDefaultSite,
        Reach.InstanceWide => isAdmin && isDefaultSite,
        Reach.MultiSite => isAdmin || multiSiteEnabled,
        Reach.MultiSiteEnabledAdmin => isAdmin && multiSiteEnabled,
        _ => false,
    };

    private readonly record struct Indexed(AppSearchEntry Entry, Reach Reach);

    /// <summary>Every entry, before any visibility filtering. Exposed so tests can hold the index to
    /// the markup: an anchor that no longer exists is a result that goes nowhere.</summary>
    internal static IEnumerable<AppSearchEntry> AllEntries => Index.Select(i => i.Entry);

    private static Indexed Entry(
        string tab, string section, string anchor, string title,
        string[] aliases, string[] keywords, Reach reach = Reach.Anyone) =>
        new(new AppSearchEntry
        {
            Title = title,
            Area = SettingsArea,
            Section = section,
            Route = $"/settings?tab={tab}",
            Anchor = anchor,
            Key = tab,
            Aliases = aliases,
            Keywords = keywords,
        }, reach);

    private static readonly Indexed[] Index =
    [
        Entry("connection", "Connection", "console-connection",
            "UniFi Console (Controller) Connection",
            ["UniFi Console Connection", "UniFi Controller", "console login"],
            ["console url", "authentication method", "local account", "network api key", "username",
             "password", "list sites", "remember credentials", "ignore ssl certificate errors",
             "self-signed certificate", "test connection", "disconnect", "udm", "ucg", "udr", "cloud key",
             "unifi os", "network server", "integrations", "super admin", "local access only"]),

        Entry("connection", "Connection", "gateway-ssh",
            "Gateway SSH (Optional)",
            ["Gateway SSH"],
            ["gateway host", "ip address", "ssh port", "username", "password", "private key path",
             "enable gateway ssh access", "test ssh connection", "iperf3 status", "start iperf3 server",
             "root", "control plane", "console ssh", "adaptive sqm", "gateway wan speed test", "uxg"]),

        Entry("connection", "Connection", "device-ssh",
            "Device SSH (Optional)",
            ["Device SSH", "UniFi device SSH"],
            ["access points", "switches", "shared credentials", "device ssh authentication",
             "port", "username", "password", "private key path", "lan speed test", "iperf3",
             "custom speed test devices", "device updates and settings"]),

        Entry("connection", "Connection", "ssh-key",
            "Managed SSH Key (Optional)",
            ["Managed SSH Key", "SSH key"],
            ["generate ssh key", "ed25519", "upload an existing key", "private key", "passphrase",
             "public key", "authorized_keys", "install on gateway", "remove from gateway",
             "speed test devices", "key instead of a password"]),

        Entry("monitoring", "Monitoring", "monitoring",
            "Monitoring",
            ["SNMP", "InfluxDB"],
            ["snmp status", "snmp credentials", "per-port counters", "device health", "3d lan map",
             "influxdb url", "api token", "organization", "primary bucket", "long-term bucket",
             "time-series storage", "setup helper", "buckets", "poll failures"]),

        Entry("monitoring", "Monitoring", "sqm-monitor",
            "Adaptive SQM Monitor (Optional)",
            ["Adaptive SQM Monitor", "SQM monitor"],
            ["sqm rates", "gateway", "port", "8088", "dashboard", "adaptive sqm page"]),

        Entry("monitoring", "Monitoring", "cellular-modem",
            "Cellular Modem (5G/LTE)",
            ["Cellular Modem", "Cellular Stats"],
            ["5g", "lte", "signal strength", "cell info", "u5g-max", "u5g backup",
             "ubiquiti modem", "netgear nighthawk hotspot", "gl-inet", "quectel", "qmicli",
             "qmi device path", "usb bus path", "ssh credentials", "scan for modems",
             "auto-discovered modems", "polling interval", "show the cellular stats tab"]),

        Entry("monitoring", "Monitoring", "cable-modem",
            "Cable Modem Monitoring",
            ["Cable Modem", "CM Stats"],
            ["docsis", "snr", "error rates", "signal quality", "provider", "host", "status page path",
             "polling interval", "show the cm stats tab", "arris surfboard", "hnap", "motorola",
             "netgear nighthawk cm", "technicolor cga", "vodafone station", "xfinity gateway", "cox"]),

        Entry("monitoring", "Monitoring", "ont-monitoring",
            "ONT Device Monitoring",
            ["ONT Monitoring", "ONT Stats"],
            ["fiber optic terminal", "signal levels", "optical", "temperature", "link state",
             "attach to sfp module", "private key path", "polling interval", "pon", "gpon",
             "show the ont stats tab", "at&t gateway", "realtek ont stick", "8311 community firmware",
             "quantum fiber q1000k", "telekom glasfaser-modem", "nokia xs-010x-q", "zyxel gpon-sfp",
             "network optimizer custom", "generic http ont"]),

        Entry("monitoring", "Monitoring", "starlink",
            "Starlink Monitoring",
            ["Starlink", "Starlink Stats"],
            ["dish", "grpc", "power draw", "obstruction", "outages", "alignment", "terminal",
             "polling interval", "show the starlink stats tab"]),

        Entry("speedtests", "Speed Tests", "speed-test-settings",
            "Speed Test Settings",
            ["iperf3 settings"],
            ["iperf3", "parallel streams by device type", "gateway", "unifi devices", "other devices",
             "test duration", "tcp streams", "throughput"]),

        Entry("speedtests", "Speed Tests", "external-speedtest-settings",
            "External Speed Test Servers",
            ["WAN speed test servers", "OpenSpeedTest servers"],
            ["openspeedtest", "vps", "remote server", "client wan test", "server name",
             "server id", "hostname", "port", "scheme", "https", "deploy commands", "set default",
             "results reported back"]),

        Entry("security", "Security & Alerts", "security-audit",
            "Security Audit",
            ["audit settings"],
            ["device placement", "vlan", "main network", "corporate vlan", "apple tv", "homepod",
             "roku", "fire tv", "chromecast", "smart tv", "media players", "printers",
             "unused port grace period", "named port grace period", "dnat dns coverage",
             "excluded vlans", "trusted dns redirect targets", "third-party dns management",
             "pi-hole", "adguard home", "technitium"]),

        Entry("security", "Security & Alerts", "alert-channels",
            "Alert Channels",
            ["Notification Channels", "Site-Specific Alert Channels"],
            ["email", "smtp host", "smtp port", "from address", "to addresses", "webhook url",
             "slack", "discord", "microsoft teams", "ntfy", "topic", "access token",
             "hmac signature", "minimum severity", "digest frequency", "day of week", "alert engine"]),

        Entry("security", "Security & Alerts", "threat-intelligence",
            "Threat Intelligence",
            ["IPS", "IDS"],
            ["threat event collection", "ips", "ids", "attack patterns", "unifi api",
             "background collection interval", "data retention"]),

        // Instance-wide security cards. The tab is on every site, but these two are only rendered on the
        // main site because the databases and credentials behind them are shared by the whole install.
        Entry("security", "Security & Alerts", "maxmind",
            "MaxMind GeoIP",
            ["GeoIP", "GeoLite2"],
            ["maxmind account id", "maxmind license key", "geolite2 database", "country", "city",
             "asn", "geographic analysis", "download now", "auto-download"], Reach.DefaultSite),

        Entry("security", "Security & Alerts", "crowdsec",
            "CrowdSec CTI",
            ["CrowdSec"],
            ["cti api key", "daily quota", "reputation database", "enrichment", "community threat intelligence",
             "source ip lookup", "usage today"], Reach.DefaultSite),

        Entry("application", "Application", "admin-password",
            "Admin Password",
            ["app password", "application password"],
            ["new password", "confirm password", "clear database password", "app_password",
             "environment variable", "auto-generated", "protect access"], Reach.InstanceWide),

        Entry("application", "Application", "licensing",
            "Licensing",
            ["license"],
            ["license key", "activate", "licensed site slots", "free slots", "free tier",
             "allowance", "term expires", "pricing"], Reach.InstanceWide),

        Entry("application", "Application", "application-settings",
            "Application Settings",
            [],
            ["pre-release update notifications", "beta", "preview build", "update banner",
             "stable updates"], Reach.InstanceWide),

        Entry("application", "Application", "guided-tours",
            "Guided Tours",
            ["What's new", "tour"],
            ["walkthrough", "replay", "start tour", "don't offer tours automatically",
             "release walkthrough"], Reach.InstanceWide),

        Entry("application", "Application", "ui-display",
            "UI / Display Settings",
            ["display settings"],
            ["kiosk mode", "side menu", "hamburger", "full width", "mini display", "per device",
             "collapse menu"], Reach.InstanceWide),

        Entry("application", "Application", "map",
            "Satellite Imagery",
            ["Mapbox", "map"],
            ["mapbox public token", "esri world imagery", "satellite layer", "speed test map",
             "signal map", "commercial use", "non-commercial"], Reach.InstanceWide),

        Entry("application", "Application", "data-management",
            "Data Management",
            ["backup", "restore", "export", "import"],
            ["back up", "full export", "settings only", "encrypted backup", ".nopt", "restore settings",
             "apply import", "clear cache", "audit history", "dismissed issues", "speed test results",
             "reset all settings", "reset to defaults"], Reach.InstanceWide),

        Entry("identity", "Identity", "identity-access",
            "Access",
            ["site access", "permissions"],
            ["site role", "grant", "membership", "who can reach which site", "site admin",
             "site operator", "site viewer", "everyone reaches every site",
             "restrict sites to members"]),

        Entry("identity", "Identity", "identity-roles",
            "Roles Reference",
            ["roles"],
            ["role", "admin", "operator", "viewer", "site admin", "site operator", "site viewer",
             "what each role can do", "global role"]),

        // Users and Sign-In only render in the instance-wide Identity view, which is the main site's
        // global Admin. A site-scoped Identity tab shows Access and the roles reference and nothing else.
        Entry("identity", "Identity", "identity-users",
            "Users",
            ["accounts", "user management"],
            ["add a user", "create user", "username", "display name", "role", "reset password",
             "disable account", "delete user", "link identity", "subject at the provider"], Reach.InstanceWide),

        Entry("identity", "Identity", "identity-sign-in",
            "Sign-In",
            ["SSO", "single sign-on", "MFA", "two-factor"],
            ["identity provider", "oidc", "openid connect", "saml", "unifi identity",
             "multi-factor authentication", "authenticator app", "passkey", "authority",
             "client id", "client secret", "scopes", "claim mapping", "login button label",
             "create on first sign-in", "local sign-in", "assertion consumer service", "acs",
             "idp metadata url", "groups claim"], Reach.InstanceWide),

        Entry("auditlog", "Audit Log", "audit-log",
            "Audit Log",
            ["activity log", "who did what"],
            ["audit events", "actor", "action", "target", "category", "outcome", "success",
             "denied", "failure", "export csv", "export json", "filter", "site id"], Reach.InstanceWide),

        Entry("multisite", "Multi-Site", "multi-site",
            "Multi-Site Management",
            ["Sites", "site management", "agents"],
            ["enable multi-site management", "add agent", "new token", "enrollment token",
             "set up agent", "update agent", "switch to site", "rename site", "site users",
             "site configuration", "unifi console through the agent tunnel", "ssh via agent",
             "lan speed test server", "client speed test target override", "agent collects for this site",
             "multi-wan vantage", "disable multi-site management", "on-site agent"], Reach.MultiSite),

        Entry("multisite", "Multi-Site", "site-agent-setup",
            "Add a site",
            ["new site", "site setup wizard"],
            ["create site", "connect unifi console", "set up an agent", "create agent token",
             "no agent needed", "skip for now", "site id"], Reach.MultiSiteEnabledAdmin),
    ];
}
