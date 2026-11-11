namespace NetworkOptimizer.Web.Services.Tailscale;

/// <summary>The control server a site's tailnet is read from.</summary>
public enum TailnetControlServer
{
    /// <summary>tailscale.com, through the Tailscale API with an OAuth credential.</summary>
    Tailscale,

    /// <summary>A self-hosted Headscale server, through its API with an API key.</summary>
    Headscale,
}

/// <summary>A site's Tailscale settings as the form shows them; secrets are never read back.</summary>
public sealed record TailscaleSettings
{
    public bool Enabled { get; init; }
    public TailnetControlServer ControlServer { get; init; } = TailnetControlServer.Tailscale;

    /// <summary>The OAuth client ID (Tailscale).</summary>
    public string ClientId { get; init; } = "";

    /// <summary>The tailnet ID; empty means "-", the OAuth client's own tailnet.</summary>
    public string Tailnet { get; init; } = "";

    public string HeadscaleUrl { get; init; } = "";

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
    public const string ControlServer = "tailscale.control_server";
    public const string ClientId = "tailscale.client_id";
    public const string ClientSecret = "tailscale.client_secret";
    public const string Tailnet = "tailscale.tailnet";
    public const string HeadscaleUrl = "tailscale.headscale_url";
    public const string HeadscaleKey = "tailscale.headscale_key";
}
