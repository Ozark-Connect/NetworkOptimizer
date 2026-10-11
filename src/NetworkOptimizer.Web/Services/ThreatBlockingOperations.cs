using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>A block this site enforces because another site in its sharing group raised it.</summary>
public sealed record SharedBlock(string SiteSlug, ThreatBlock Block);

/// <summary>What the Blocked Sources card and the Automatic Blocking card show for one site.</summary>
public sealed record BlockingOverview(
    BlockingSettings Settings,
    IReadOnlyList<ThreatBlock> Active,
    IReadOnlyList<ThreatBlock> Recent,
    IReadOnlyList<SharedBlock> Shared,
    IReadOnlyList<BlockTargetStatus> Targets);

/// <summary>What Turn On Recommended enabled. <see cref="GatewayError"/> says why the gateway was left out.</summary>
public sealed record RecommendedResult(bool Gateway, string? GatewayError, bool Waf);

/// <summary>Cloudflare target state for the Settings card.</summary>
public sealed record CloudflareBlockingStatus(bool HasToken, string? AccountId, CloudflareList? List, string? Error);

/// <summary>
/// Automatic Blocking for any one site, by slug. Not gated: only the gated services
/// (<see cref="IThreatBlockingAdmin"/> for the site in context, <see cref="IThreatBlockingSiteAdmin"/>
/// for a named site) may call it, so every path is authorized and audited there.
/// </summary>
public sealed class ThreatBlockingOperations
{
    private readonly IThreatSettingsAccessor _settings;
    private readonly ThreatBlockingService _blocking;
    private readonly GatewayBlocklist _gateway;
    private readonly IBlockSharingScope _sharing;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? _siteContext;
    private readonly ICredentialProtectionService _credentials;
    private readonly IHttpClientFactory _httpClientFactory;

    public ThreatBlockingOperations(
        IThreatSettingsAccessor settings,
        ThreatBlockingService blocking,
        GatewayBlocklist gateway,
        IBlockSharingScope sharing,
        IServiceScopeFactory scopeFactory,
        ICredentialProtectionService credentials,
        IHttpClientFactory httpClientFactory,
        NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? siteContext = null)
    {
        _settings = settings;
        _blocking = blocking;
        _gateway = gateway;
        _sharing = sharing;
        _scopeFactory = scopeFactory;
        _credentials = credentials;
        _httpClientFactory = httpClientFactory;
        _siteContext = siteContext;
    }

    // Blocking code calls the default site null; it keeps the bare settings keys.
    private string? Site(string slug) => _siteContext == null || slug == _siteContext.DefaultKey ? null : slug;

    private string Key(string slug, string baseKey) => BlockingSettingKeys.ForSite(baseKey, Site(slug));

    // A scope whose repository reads the given site's database.
    private IServiceScope ScopeFor(string? site)
    {
        var scope = _scopeFactory.CreateScope();
        _siteContext?.PinScope(scope, site ?? _siteContext.DefaultKey);
        return scope;
    }

    public Task<BlockingSettings> GetSettingsAsync(string slug, CancellationToken ct) =>
        BlockingSettings.LoadAsync(k => _settings.GetSettingAsync(k, ct), Site(slug));

    public async Task<BlockingOverview> GetOverviewAsync(string slug, CancellationToken ct)
    {
        var site = Site(slug);
        var config = await GetSettingsAsync(slug, ct);
        var now = DateTime.UtcNow;
        List<ThreatBlock> blocks;
        using (var scope = ScopeFor(site))
            blocks = await scope.ServiceProvider.GetRequiredService<IThreatRepository>().GetBlocksAsync(now, now.AddDays(-1), ct);

        var shared = new List<SharedBlock>();
        if (config.Share && _siteContext != null)
        {
            foreach (var peer in await _sharing.GetPeerSitesAsync(site, ct))
            {
                if (peer == site) continue;
                using var scope = ScopeFor(peer);
                var peerBlocks = await scope.ServiceProvider.GetRequiredService<IThreatRepository>().GetBlocksAsync(now, now, ct);
                shared.AddRange(peerBlocks.Where(b => b.Enforced && b.IsActive(now))
                    .Select(b => new SharedBlock(peer ?? _siteContext.DefaultKey, b)));
            }
        }

        return new BlockingOverview(config,
            blocks.Where(b => b.IsActive(now)).ToList(),
            blocks.Where(b => !b.IsActive(now)).ToList(),
            shared,
            _blocking.TargetStatus(site));
    }

    public async Task SaveSettingsAsync(string slug, BlockingSettings s, CancellationToken ct)
    {
        var invalid = BlockAllowlist.Invalid(s.Allowlist);
        if (invalid.Count > 0)
            throw new ArgumentException($"Not an IP address or CIDR range: {string.Join(", ", invalid.Take(5))}");

        async Task Set(string key, string value) => await _settings.SaveSettingAsync(Key(slug, key), value, ct);
        await Set(BlockingSettingKeys.Mode, s.Mode.ToString());
        await Set(BlockingSettingKeys.WafEventThreshold, Math.Clamp(s.WafEventThreshold, BlockingSettings.MinThreshold, BlockingSettings.MaxThreshold).ToString());
        await Set(BlockingSettingKeys.WindowMinutes, Math.Clamp(s.WindowMinutes, 1, BlockingSettings.MaxWindowMinutes).ToString());
        await Set(BlockingSettingKeys.UsePatterns, s.UsePatterns.ToString());
        await Set(BlockingSettingKeys.BanMinutes, Math.Clamp(s.BanMinutes, 1, BlockingSettings.MaxBanMinutes).ToString());
        await Set(BlockingSettingKeys.MaxEntries, Math.Clamp(s.MaxEntries, 1, BlockingSettings.MaxMaxEntries).ToString());
        await Set(BlockingSettingKeys.Allowlist, string.Join("\n", BlockAllowlist.ParseList(s.Allowlist)));
        await Set(BlockingSettingKeys.Share, s.Share.ToString());
        await Set(BlockingSettingKeys.TargetGateway, s.TargetGateway.ToString());
        // The WAF is read on the default site only.
        await Set(BlockingSettingKeys.TargetWaf, (s.TargetWaf && Site(slug) == null).ToString());
        await Set(BlockingSettingKeys.TargetCloudflare, s.TargetCloudflare.ToString());
        _blocking.Trigger();
    }

    public async Task<RecommendedResult> TurnOnRecommendedAsync(string slug, CancellationToken ct)
    {
        var current = await GetSettingsAsync(slug, ct);
        string? gatewayError = null;
        try
        {
            await _gateway.SetUpAsync(Site(slug), ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
                                   or NetworkOptimizer.UniFi.UniFiApiErrorException or NetworkOptimizer.UniFi.UniFiPermissionException)
        {
            gatewayError = ex.Message;
        }
        var waf = Site(slug) == null && await BlockingSettings.WafConnectedAsync(k => _settings.GetSettingAsync(k, ct));

        await SaveSettingsAsync(slug, current with
        {
            Mode = BlockingMode.Enforce,
            UsePatterns = true,
            Share = true,
            TargetGateway = gatewayError == null,
            TargetWaf = waf,
        }, ct);
        return new RecommendedResult(gatewayError == null, gatewayError, waf);
    }

    /// <summary>Manual block lengths offered next to an address, in minutes.</summary>
    public static readonly IReadOnlyList<int> ManualBlockMinutes = [60, 1440, BlockingSettings.MaxBanMinutes];

    /// <summary>
    /// Blocks an address by hand. It takes effect in Dry Run as well as Enforce (Dry Run governs
    /// automatic decisions only), and is refused for anything the allowlist protects.
    /// </summary>
    public async Task<ThreatBlock> BlockAsync(string slug, string ip, int minutes, string blockedBy, CancellationToken ct)
    {
        // One spelling per address, so IPv6 and IPv4-mapped forms match the blocks already stored.
        var normalized = BlockAllowlist.Normalize(ip) ?? throw new ArgumentException($"{ip} is not an IP address.");

        var site = Site(slug);
        var settings = await GetSettingsAsync(slug, ct);
        using var scope = ScopeFor(site);
        var repository = scope.ServiceProvider.GetRequiredService<IThreatRepository>();
        var allowlist = new BlockAllowlist(settings.Allowlist, await repository.GetNoiseFiltersAsync(ct));
        if (allowlist.Exempts(normalized) is { } reason)
            throw new ArgumentException($"{normalized} can't be blocked: it is {reason}.");

        var now = DateTime.UtcNow;
        if ((await repository.GetBlocksAsync(now, now, ct)).Any(b => b.IsActive(now) && b.Enforced
                && string.Equals(b.SourceIp, normalized, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"{normalized} is already blocked.");

        var block = new ThreatBlock
        {
            SourceIp = normalized,
            Trigger = BlockTrigger.Manual,
            Reason = $"Blocked by {blockedBy}",
            Enforced = true,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Math.Clamp(minutes, 1, BlockingSettings.MaxBanMinutes)),
        };
        await repository.AddBlocksAsync([block], ct);
        _blocking.Trigger();
        return block;
    }

    /// <summary>The active block on an address at this site, if any (Dry Run records included).</summary>
    public async Task<ThreatBlock?> FindActiveBlockAsync(string slug, string ip, CancellationToken ct)
    {
        if (BlockAllowlist.Normalize(ip) is not { } normalized) return null;
        using var scope = ScopeFor(Site(slug));
        var now = DateTime.UtcNow;
        return (await scope.ServiceProvider.GetRequiredService<IThreatRepository>().GetBlocksAsync(now, now, ct))
            .Where(b => b.IsActive(now) && string.Equals(b.SourceIp, normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(b => b.Enforced)
            .FirstOrDefault();
    }

    public async Task<bool> ReleaseAsync(string slug, int blockId, string releasedBy, CancellationToken ct)
    {
        using var scope = ScopeFor(Site(slug));
        var released = await scope.ServiceProvider.GetRequiredService<IThreatRepository>()
            .ReleaseBlockAsync(blockId, releasedBy, DateTime.UtcNow, ct);
        if (released) _blocking.Trigger();
        return released;
    }

    public async Task<GatewaySetUpResult> SetUpGatewayAsync(string slug, CancellationToken ct)
    {
        var result = await _gateway.SetUpAsync(Site(slug), ct);
        _blocking.Trigger();
        return result;
    }

    public Task RemoveGatewayAsync(string slug, CancellationToken ct) => _gateway.RemoveAsync(Site(slug), ct);

    /// <summary>True when Set Up has run for the site (whether or not the objects are still intact).</summary>
    public async Task<bool> IsGatewaySetUpAsync(string slug, CancellationToken ct) =>
        await _gateway.LoadStateAsync(Site(slug), ct) != null;

    /// <summary>Null when the gateway objects are set up and intact, otherwise why not.</summary>
    public async Task<string?> CheckGatewayAsync(string slug, CancellationToken ct)
    {
        if (!await IsGatewaySetUpAsync(slug, ct))
            return "Not set up yet.";
        try
        {
            return await _gateway.VerifyAsync(Site(slug), ct);
        }
        catch (Exception ex) when (ex is NetworkOptimizer.UniFi.UniFiApiErrorException or InvalidOperationException or HttpRequestException)
        {
            return ex.Message;
        }
    }

    public async Task<CloudflareList> SetUpCloudflareAsync(string slug, string? token, string accountId, CancellationToken ct)
    {
        accountId = accountId.Trim();
        if (string.IsNullOrEmpty(accountId))
            throw new ArgumentException("Enter the Cloudflare Account ID.");
        var useToken = string.IsNullOrWhiteSpace(token)
            ? await _settings.GetDecryptedSettingAsync(Key(slug, BlockingSettingKeys.CloudflareToken), ct)
            : token.Trim();
        if (string.IsNullOrEmpty(useToken))
            throw new ArgumentException("Enter a Cloudflare API token.");

        var listId = await _settings.GetSettingAsync(Key(slug, BlockingSettingKeys.CloudflareListId), ct);
        var client = new CloudflareListClient(_httpClientFactory.CreateClient("Cloudflare"));
        var list = await client.EnsureListAsync(useToken, accountId, listId, ct);

        if (!string.IsNullOrWhiteSpace(token))
            await _settings.SaveSettingAsync(Key(slug, BlockingSettingKeys.CloudflareToken), _credentials.Encrypt(useToken), ct);
        await _settings.SaveSettingAsync(Key(slug, BlockingSettingKeys.CloudflareAccountId), accountId, ct);
        await _settings.SaveSettingAsync(Key(slug, BlockingSettingKeys.CloudflareListId), list.Id, ct);
        _blocking.Trigger();
        return list;
    }

    public async Task<CloudflareBlockingStatus> GetCloudflareStatusAsync(string slug, CancellationToken ct)
    {
        var token = await _settings.GetDecryptedSettingAsync(Key(slug, BlockingSettingKeys.CloudflareToken), ct);
        var account = await _settings.GetSettingAsync(Key(slug, BlockingSettingKeys.CloudflareAccountId), ct);
        var listId = await _settings.GetSettingAsync(Key(slug, BlockingSettingKeys.CloudflareListId), ct);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(account) || string.IsNullOrEmpty(listId))
            return new CloudflareBlockingStatus(!string.IsNullOrEmpty(token), account, null, null);
        try
        {
            var list = await new CloudflareListClient(_httpClientFactory.CreateClient("Cloudflare"))
                .GetListAsync(token, account, listId, ct);
            return new CloudflareBlockingStatus(true, account, list, null);
        }
        catch (CloudflareException ex)
        {
            return new CloudflareBlockingStatus(true, account, null, ex.Message);
        }
    }
}
