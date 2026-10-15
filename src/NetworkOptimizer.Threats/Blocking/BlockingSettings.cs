namespace NetworkOptimizer.Threats.Blocking;

/// <summary>Automatic Blocking modes.</summary>
public enum BlockingMode
{
    Off = 0,

    /// <summary>Decide and record, but send nothing to any target.</summary>
    DryRun = 1,

    Enforce = 2
}

/// <summary>
/// SystemSettings keys for Automatic Blocking, one set per site: each site has its own gateway and
/// may have its own Cloudflare account. Stored in the main database like the collector's per-site
/// state; use <see cref="ForSite"/> to get a site's key.
/// </summary>
public static class BlockingSettingKeys
{
    /// <summary>The key for a site. Null or empty is the default site, which keeps the bare key.</summary>
    public static string ForSite(string key, string? site) => string.IsNullOrEmpty(site) ? key : $"{key}:{site}";

    public const string Mode = "blocking.mode";
    public const string WafEventThreshold = "blocking.waf_event_threshold";
    public const string WindowMinutes = "blocking.window_minutes";
    public const string UsePatterns = "blocking.use_patterns";
    public const string BanMinutes = "blocking.ban_minutes";
    public const string MaxEntries = "blocking.max_entries";
    /// <summary>
    /// Addresses and ranges never blocked that grant no access (unlike Trusted Sources' extra
    /// addresses), one per line or comma-separated.
    /// </summary>
    public const string Allowlist = "blocking.allowlist";
    /// <summary>"true" to contribute this site's blocks to, and enforce, the shared group's.</summary>
    public const string Share = "blocking.share";
    /// <summary>Comma-separated <see cref="PublicBlockLists"/> ids. Unset = the lists that are on by default.</summary>
    public const string Feeds = "blocking.feeds";
    /// <summary>"true" to also block devices from reaching the public lists' addresses (gateway only).</summary>
    public const string FeedsOutbound = "blocking.feeds_outbound";
    /// <summary>"true" once a Site Admin hides the Protection Setup checklist on Threat Intelligence.</summary>
    public const string SetupHidden = "blocking.setup_hidden";

    public const string TargetGateway = "blocking.target.gateway";
    public const string TargetWaf = "blocking.target.waf";
    public const string TargetCloudflare = "blocking.target.cloudflare";

    /// <summary>JSON <see cref="GatewayBlocklistState"/>: the console objects Network Optimizer owns.</summary>
    public const string GatewayState = "blocking.gateway.state";

    /// <summary>Cloudflare API token, stored encrypted.</summary>
    public const string CloudflareToken = "blocking.cloudflare.token";
    public const string CloudflareAccountId = "blocking.cloudflare.account_id";
    public const string CloudflareListId = "blocking.cloudflare.list_id";
}

/// <summary>Automatic Blocking configuration, with the defaults used when a key is unset.</summary>
public sealed record BlockingSettings
{
    /// <summary>
    /// Dry Run until someone turns on Enforce: it writes nothing to any console or edge, so every
    /// site can start it on upgrade, and the first Enforce is always a deliberate click.
    /// </summary>
    public BlockingMode Mode { get; init; } = BlockingMode.DryRun;

    /// <summary>WAF events from one source within <see cref="WindowMinutes"/> that trigger a block.</summary>
    public int WafEventThreshold { get; init; } = 5;

    public int WindowMinutes { get; init; } = 10;

    /// <summary>Also block sources of scan sweep, brute force, and exploit campaign patterns.</summary>
    public bool UsePatterns { get; init; } = true;

    /// <summary>First ban length; each earlier strike within 30 days multiplies it by 4, up to 7 days.</summary>
    public int BanMinutes { get; init; } = 60;

    /// <summary>Most entries sent to a target. The UniFi console slows down on large groups.</summary>
    public int MaxEntries { get; init; } = 500;

    public string Allowlist { get; init; } = "";

    /// <summary>Share blocks with the other sites that share (see <see cref="IBlockSharingScope"/>). On unless a site opts out.</summary>
    public bool Share { get; init; } = true;

    /// <summary>The public block lists this site enforces (in Enforce only, on the gateway and the WAF).</summary>
    public IReadOnlyList<string> Feeds { get; init; } = PublicBlockLists.DefaultIds;

    /// <summary>Also block outbound connections to the public lists on the gateway. Optional, off by default.</summary>
    public bool FeedsOutbound { get; init; }

    public bool TargetGateway { get; init; }
    public bool TargetWaf { get; init; }
    public bool TargetCloudflare { get; init; }

    public const int MinThreshold = 1;
    public const int MaxThreshold = 1000;
    public const int MaxWindowMinutes = 1440;
    public const int MaxBanMinutes = 10080;
    public const int MaxMaxEntries = 10000;

    /// <summary>
    /// Reads a site's settings (null = default site), falling back to defaults. The WAF target
    /// follows the WAF connection until someone sets it: connecting a WAF turns its ban list on.
    /// </summary>
    public static async Task<BlockingSettings> LoadAsync(Func<string, Task<string?>> getRaw, string? site)
    {
        var settings = await LoadAsync(k => getRaw(BlockingSettingKeys.ForSite(k, site)));
        if (await getRaw(BlockingSettingKeys.ForSite(BlockingSettingKeys.TargetWaf, site)) == null)
            settings = settings with { TargetWaf = await WafConnectedAsync(getRaw, site) };
        return settings;
    }

    /// <summary>Whether a site reads a WAF (enabled, with a URL and a token). Null site = default.</summary>
    public static async Task<bool> WafConnectedAsync(Func<string, Task<string?>> getRaw, string? site) =>
        string.Equals(await getRaw(Waf.WafSettingKeys.ForSite(Waf.WafSettingKeys.Enabled, site)), "true", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(await getRaw(Waf.WafSettingKeys.ForSite(Waf.WafSettingKeys.Url, site)))
        && !string.IsNullOrWhiteSpace(await getRaw(Waf.WafSettingKeys.ForSite(Waf.WafSettingKeys.Token, site)));

    /// <summary>Reads the settings, falling back to defaults for unset or unreadable values.</summary>
    public static async Task<BlockingSettings> LoadAsync(Func<string, Task<string?>> get)
    {
        var d = new BlockingSettings();
        return new BlockingSettings
        {
            Mode = Enum.TryParse<BlockingMode>(await get(BlockingSettingKeys.Mode), true, out var m) ? m : d.Mode,
            WafEventThreshold = Int(await get(BlockingSettingKeys.WafEventThreshold), d.WafEventThreshold, MinThreshold, MaxThreshold),
            WindowMinutes = Int(await get(BlockingSettingKeys.WindowMinutes), d.WindowMinutes, 1, MaxWindowMinutes),
            UsePatterns = Bool(await get(BlockingSettingKeys.UsePatterns), d.UsePatterns),
            BanMinutes = Int(await get(BlockingSettingKeys.BanMinutes), d.BanMinutes, 1, MaxBanMinutes),
            MaxEntries = Int(await get(BlockingSettingKeys.MaxEntries), d.MaxEntries, 1, MaxMaxEntries),
            Allowlist = await get(BlockingSettingKeys.Allowlist) ?? "",
            Share = Bool(await get(BlockingSettingKeys.Share), d.Share),
            Feeds = await get(BlockingSettingKeys.Feeds) is { } feeds
                ? feeds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(id => PublicBlockLists.Find(id) != null).Distinct().ToList()
                : d.Feeds,
            FeedsOutbound = Bool(await get(BlockingSettingKeys.FeedsOutbound), false),
            TargetGateway = Bool(await get(BlockingSettingKeys.TargetGateway), false),
            TargetWaf = Bool(await get(BlockingSettingKeys.TargetWaf), false),
            TargetCloudflare = Bool(await get(BlockingSettingKeys.TargetCloudflare), false),
        };
    }

    private static int Int(string? v, int fallback, int min, int max) =>
        int.TryParse(v, out var n) ? Math.Clamp(n, min, max) : fallback;

    private static bool Bool(string? v, bool fallback) =>
        bool.TryParse(v, out var b) ? b : fallback;
}
