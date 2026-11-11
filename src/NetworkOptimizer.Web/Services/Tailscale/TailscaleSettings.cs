namespace NetworkOptimizer.Web.Services.Tailscale;

/// <summary>Where a site's list of tailnet devices comes from.</summary>
public enum TailnetDeviceSource
{
    /// <summary>The Tailscale API, with an OAuth client.</summary>
    TailscaleApi,

    /// <summary>A self-hosted Headscale control server's API.</summary>
    HeadscaleApi,

    /// <summary>The peers a local tailscaled knows, with no API.</summary>
    LocalStatus,
}

/// <summary>Which host's tailscaled is read for connection paths and routed-traffic attribution.</summary>
public enum TailnetLocalSource
{
    /// <summary>No local status: names and routes come from the API only.</summary>
    Off,

    /// <summary>The tailscaled on the host Network Optimizer runs on.</summary>
    Server,

    /// <summary>The tailscaled on the host of the site's On-Site Agent.</summary>
    Agent,
}

/// <summary>A site's Tailscale settings as the form shows them; secrets are never read back.</summary>
public sealed record TailscaleSettings
{
    public bool Enabled { get; init; }
    public TailnetDeviceSource DeviceSource { get; init; } = TailnetDeviceSource.TailscaleApi;

    /// <summary>The OAuth client ID (Tailscale API).</summary>
    public string ClientId { get; init; } = "";

    /// <summary>The tailnet ID; empty means "-", the OAuth client's own tailnet.</summary>
    public string Tailnet { get; init; } = "";

    public string HeadscaleUrl { get; init; } = "";
    public TailnetLocalSource LocalSource { get; init; } = TailnetLocalSource.Off;

    /// <summary>The tailscaled socket path on the host <see cref="LocalSource"/> names; empty for the platform default.</summary>
    public string SocketPath { get; init; } = "";

    /// <summary>Whether to read the local host's conntrack to attribute routed traffic to its peer.</summary>
    public bool AttributeRoutedTraffic { get; init; }

    /// <summary>Whether a client secret (Tailscale) is stored.</summary>
    public bool HasClientSecret { get; init; }

    /// <summary>Whether a Headscale API key is stored.</summary>
    public bool HasHeadscaleKey { get; init; }
}

/// <summary>A save from the form: a null or empty secret keeps the stored one.</summary>
public sealed record TailscaleSettingsUpdate(TailscaleSettings Settings, string? ClientSecret, string? HeadscaleKey);

/// <summary>The SystemSettings keys, in each site's own database.</summary>
internal static class TailscaleSettingKeys
{
    public const string Enabled = "tailscale.enabled";
    public const string DeviceSource = "tailscale.device_source";
    public const string ClientId = "tailscale.client_id";
    public const string ClientSecret = "tailscale.client_secret";
    public const string Tailnet = "tailscale.tailnet";
    public const string HeadscaleUrl = "tailscale.headscale_url";
    public const string HeadscaleKey = "tailscale.headscale_key";
    public const string LocalSource = "tailscale.local_source";
    public const string SocketPath = "tailscale.socket_path";
    public const string AttributeRouted = "tailscale.attribute_routed";
}
