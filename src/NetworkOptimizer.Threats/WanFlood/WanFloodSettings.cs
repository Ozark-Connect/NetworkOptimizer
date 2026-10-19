namespace NetworkOptimizer.Threats.WanFlood;

/// <summary>WAN Flood Protection modes.</summary>
public enum WanFloodMode
{
    Off = 0,

    /// <summary>
    /// The limits are installed without their drop, and the detector reports what it would drop. Not quite
    /// drop-free: a hashlimit table that fills drops new sources whatever its rule's target.
    /// </summary>
    Detect = 1,

    Enforce = 2
}

/// <summary>
/// SystemSettings keys for WAN Flood Protection, one set per site (each site has its own gateway). Use
/// <see cref="ForSite"/> to get a site's key.
/// </summary>
public static class WanFloodSettingKeys
{
    /// <summary>The key for a site. Null or empty is the default site, which keeps the bare key.</summary>
    public static string ForSite(string key, string? site) => string.IsNullOrEmpty(site) ? key : $"{key}:{site}";

    public const string Mode = "wanflood.mode";
    public const string UdpPerSource = "wanflood.udp_per_source";
    public const string UdpBurst = "wanflood.udp_burst";
    public const string SynPerSource = "wanflood.syn_per_source";
    public const string SynBurst = "wanflood.syn_burst";
    public const string ForwardedPort = "wanflood.forwarded_port";
    public const string ForwardedBurst = "wanflood.forwarded_burst";
    public const string ForwardedCapped = "wanflood.forwarded_capped";
    public const string DropTtl = "wanflood.drop_ttl";
    public const string MaxDropTtl = "wanflood.max_drop_ttl";
    /// <summary>Addresses and ranges never limited or dropped, one per line or comma-separated.</summary>
    public const string NeverDrop = "wanflood.never_drop";
    /// <summary>
    /// "true" while the test drop is held (see the agent's WanFloodConfig.test_drop). No UI: a developer switch for
    /// checking that drops survive a Smart Queues re-provision. A Settings save keeps whatever it holds.
    /// </summary>
    public const string TestDrop = "wanflood.test_drop";
}

/// <summary>WAN Flood Protection configuration, with the defaults used when a key is unset.</summary>
public sealed record WanFloodSettings
{
    /// <summary>Off until someone turns it on.</summary>
    public WanFloodMode Mode { get; init; } = WanFloodMode.Off;

    /// <summary>New UDP connections per second from one source to the gateway, and the burst above it.</summary>
    public int UdpPerSource { get; init; } = 2000;
    public int UdpBurst { get; init; } = 4000;

    /// <summary>New TCP connections (SYN) per second from one source to the gateway, and the burst above it.</summary>
    public int SynPerSource { get; init; } = 500;
    public int SynBurst { get; init; } = 1000;

    /// <summary>New connections per second to one port forward, and the burst above it.</summary>
    public int ForwardedPort { get; init; } = 1000;
    public int ForwardedBurst { get; init; } = 2000;

    /// <summary>The per-port-forward limit while the conntrack table is under pressure.</summary>
    public int ForwardedCapped { get; init; } = 100;

    public int DropTtlSeconds { get; init; } = 600;
    public int MaxDropTtlSeconds { get; init; } = 3600;

    public string NeverDrop { get; init; } = "";

    public bool TestDrop { get; init; }

    public const int MinRate = 10;
    public const int MaxRate = 100000;
    public const int MinTtl = 60;
    public const int MaxTtl = 86400;

    /// <summary>Reads a site's settings (null = default site), falling back to defaults.</summary>
    public static Task<WanFloodSettings> LoadAsync(Func<string, Task<string?>> getRaw, string? site) =>
        LoadAsync(k => getRaw(WanFloodSettingKeys.ForSite(k, site)));

    /// <summary>Reads the settings, falling back to defaults for unset or unreadable values.</summary>
    public static async Task<WanFloodSettings> LoadAsync(Func<string, Task<string?>> get)
    {
        var d = new WanFloodSettings();
        return new WanFloodSettings
        {
            Mode = Enum.TryParse<WanFloodMode>(await get(WanFloodSettingKeys.Mode), true, out var m) ? m : d.Mode,
            UdpPerSource = Int(await get(WanFloodSettingKeys.UdpPerSource), d.UdpPerSource, MinRate, MaxRate),
            UdpBurst = Int(await get(WanFloodSettingKeys.UdpBurst), d.UdpBurst, MinRate, MaxRate),
            SynPerSource = Int(await get(WanFloodSettingKeys.SynPerSource), d.SynPerSource, MinRate, MaxRate),
            SynBurst = Int(await get(WanFloodSettingKeys.SynBurst), d.SynBurst, MinRate, MaxRate),
            ForwardedPort = Int(await get(WanFloodSettingKeys.ForwardedPort), d.ForwardedPort, MinRate, MaxRate),
            ForwardedBurst = Int(await get(WanFloodSettingKeys.ForwardedBurst), d.ForwardedBurst, MinRate, MaxRate),
            ForwardedCapped = Int(await get(WanFloodSettingKeys.ForwardedCapped), d.ForwardedCapped, MinRate, MaxRate),
            DropTtlSeconds = Int(await get(WanFloodSettingKeys.DropTtl), d.DropTtlSeconds, MinTtl, MaxTtl),
            MaxDropTtlSeconds = Int(await get(WanFloodSettingKeys.MaxDropTtl), d.MaxDropTtlSeconds, MinTtl, MaxTtl),
            NeverDrop = await get(WanFloodSettingKeys.NeverDrop) ?? "",
            TestDrop = Bool(await get(WanFloodSettingKeys.TestDrop), false),
        };
    }

    /// <summary>The settings with every number clamped to its range, as they are saved.</summary>
    public WanFloodSettings Clamped() => this with
    {
        UdpPerSource = Math.Clamp(UdpPerSource, MinRate, MaxRate),
        UdpBurst = Math.Clamp(UdpBurst, MinRate, MaxRate),
        SynPerSource = Math.Clamp(SynPerSource, MinRate, MaxRate),
        SynBurst = Math.Clamp(SynBurst, MinRate, MaxRate),
        ForwardedPort = Math.Clamp(ForwardedPort, MinRate, MaxRate),
        ForwardedBurst = Math.Clamp(ForwardedBurst, MinRate, MaxRate),
        ForwardedCapped = Math.Clamp(ForwardedCapped, MinRate, MaxRate),
        DropTtlSeconds = Math.Clamp(DropTtlSeconds, MinTtl, MaxTtl),
        MaxDropTtlSeconds = Math.Clamp(Math.Max(MaxDropTtlSeconds, DropTtlSeconds), MinTtl, MaxTtl),
    };

    /// <summary>The never-drop entries that parse as an address or a CIDR range.</summary>
    public IReadOnlyList<string> NeverDropEntries() => NeverDrop
        .Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(e => System.Net.IPNetwork.TryParse(e, out _) || System.Net.IPAddress.TryParse(e, out _))
        .Distinct()
        .ToList();

    private static int Int(string? v, int fallback, int min, int max) =>
        int.TryParse(v, out var n) ? Math.Clamp(n, min, max) : fallback;

    private static bool Bool(string? v, bool fallback) =>
        bool.TryParse(v, out var b) ? b : fallback;
}
