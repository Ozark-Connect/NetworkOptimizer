using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.Web.Services.Gates;

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
/// Automatic Blocking for the site in context: each site has its own gateway, may have its own
/// Cloudflare account, and opts into sharing blocks separately. Reads are Viewer (the Blocked
/// Sources card is on Threat Intelligence); every write is Site Admin and audited.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IThreatBlockingAdmin
{
    [RequireRole(Roles.Viewer)]
    Task<BlockingOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "threat_blocking")]
    Task SaveSettingsAsync(BlockingSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// One click from Dry Run (the default) to the setup a homelab wants: Enforce, patterns on,
    /// sharing on, the gateway set up and used, and the WAF used when one is connected. Cloudflare
    /// is left as it is: it needs a token.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "threat_blocking")]
    Task<RecommendedResult> TurnOnRecommendedAsync(CancellationToken cancellationToken = default);

    /// <summary>Ends one of this site's blocks now; the next sync, which this triggers, removes it everywhere.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ThreatSourceUnblocked, Category = AuditCategories.Settings, TargetType = "threat_block")]
    Task<bool> ReleaseAsync(int blockId, string releasedBy, CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ThreatBlockingTargetSetUp, Category = AuditCategories.Settings, TargetType = "gateway")]
    Task<GatewaySetUpResult> SetUpGatewayAsync(CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ThreatBlockingTargetRemoved, Category = AuditCategories.Settings, TargetType = "gateway")]
    Task RemoveGatewayAsync(CancellationToken cancellationToken = default);

    /// <summary>Null when the gateway objects are set up and intact, otherwise why not.</summary>
    [RequireRole(Roles.Admin)]
    Task<string?> CheckGatewayAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the token (when given) and account, then finds or creates the list.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ThreatBlockingTargetSetUp, Category = AuditCategories.Settings, TargetType = "cloudflare")]
    Task<CloudflareList> SetUpCloudflareAsync(string? token, string accountId, CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    Task<CloudflareBlockingStatus> GetCloudflareStatusAsync(CancellationToken cancellationToken = default);
}

public sealed class ThreatBlockingAdminService : IThreatBlockingAdmin
{
    private readonly IThreatRepository _repository;
    private readonly IThreatSettingsAccessor _settings;
    private readonly SiteContextService _site;
    private readonly ThreatBlockingService _blocking;
    private readonly GatewayBlocklist _gateway;
    private readonly IBlockSharingScope _sharing;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? _siteContext;
    private readonly ICredentialProtectionService _credentials;
    private readonly IHttpClientFactory _httpClientFactory;

    public ThreatBlockingAdminService(
        IThreatRepository repository,
        IThreatSettingsAccessor settings,
        SiteContextService site,
        ThreatBlockingService blocking,
        GatewayBlocklist gateway,
        IBlockSharingScope sharing,
        IServiceScopeFactory scopeFactory,
        ICredentialProtectionService credentials,
        IHttpClientFactory httpClientFactory,
        NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? siteContext = null)
    {
        _repository = repository;
        _settings = settings;
        _site = site;
        _blocking = blocking;
        _gateway = gateway;
        _sharing = sharing;
        _scopeFactory = scopeFactory;
        _credentials = credentials;
        _httpClientFactory = httpClientFactory;
        _siteContext = siteContext;
    }

    // The default site keeps the bare settings keys, so it is null here.
    private string? Site => _site.IsDefault ? null : _site.Slug;

    private string Key(string baseKey) => BlockingSettingKeys.ForSite(baseKey, Site);

    public async Task<BlockingOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var config = await BlockingSettings.LoadAsync(k => _settings.GetSettingAsync(k, cancellationToken), Site);
        var now = DateTime.UtcNow;
        var blocks = await _repository.GetBlocksAsync(now, now.AddDays(-1), cancellationToken);

        var shared = new List<SharedBlock>();
        if (config.Share && _siteContext != null)
        {
            foreach (var peer in await _sharing.GetPeerSitesAsync(Site, cancellationToken))
            {
                if (peer == Site) continue;
                using var scope = _scopeFactory.CreateScope();
                _siteContext.PinScope(scope, peer ?? _siteContext.DefaultKey);
                var peerBlocks = await scope.ServiceProvider.GetRequiredService<IThreatRepository>().GetBlocksAsync(now, now, cancellationToken);
                shared.AddRange(peerBlocks.Where(b => b.Enforced && b.IsActive(now))
                    .Select(b => new SharedBlock(peer ?? _siteContext.DefaultKey, b)));
            }
        }

        return new BlockingOverview(config,
            blocks.Where(b => b.IsActive(now)).ToList(),
            blocks.Where(b => !b.IsActive(now)).ToList(),
            shared,
            _blocking.TargetStatus(Site));
    }

    public async Task SaveSettingsAsync(BlockingSettings s, CancellationToken cancellationToken = default)
    {
        var invalid = BlockAllowlist.Invalid(s.Allowlist);
        if (invalid.Count > 0)
            throw new ArgumentException($"Not an IP address or CIDR range: {string.Join(", ", invalid.Take(5))}");

        async Task Set(string key, string value) => await _settings.SaveSettingAsync(Key(key), value, cancellationToken);
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
        await Set(BlockingSettingKeys.TargetWaf, (s.TargetWaf && Site == null).ToString());
        await Set(BlockingSettingKeys.TargetCloudflare, s.TargetCloudflare.ToString());
        _blocking.Trigger();
    }

    public async Task<RecommendedResult> TurnOnRecommendedAsync(CancellationToken cancellationToken = default)
    {
        var current = await BlockingSettings.LoadAsync(k => _settings.GetSettingAsync(k, cancellationToken), Site);

        string? gatewayError = null;
        try
        {
            await _gateway.SetUpAsync(Site, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
                                   or NetworkOptimizer.UniFi.UniFiApiErrorException or NetworkOptimizer.UniFi.UniFiPermissionException)
        {
            gatewayError = ex.Message;
        }
        var waf = Site == null && await BlockingSettings.WafConnectedAsync(k => _settings.GetSettingAsync(k, cancellationToken));

        // Called on this instance, not through the gate: the one audit entry for this call covers it.
        await SaveSettingsAsync(current with
        {
            Mode = BlockingMode.Enforce,
            UsePatterns = true,
            Share = true,
            TargetGateway = gatewayError == null,
            TargetWaf = waf,
        }, cancellationToken);
        return new RecommendedResult(gatewayError == null, gatewayError, waf);
    }

    public async Task<bool> ReleaseAsync(int blockId, string releasedBy, CancellationToken cancellationToken = default)
    {
        var released = await _repository.ReleaseBlockAsync(blockId, releasedBy, DateTime.UtcNow, cancellationToken);
        if (released) _blocking.Trigger();
        return released;
    }

    public async Task<GatewaySetUpResult> SetUpGatewayAsync(CancellationToken cancellationToken = default)
    {
        var result = await _gateway.SetUpAsync(Site, cancellationToken);
        _blocking.Trigger();
        return result;
    }

    public Task RemoveGatewayAsync(CancellationToken cancellationToken = default) =>
        _gateway.RemoveAsync(Site, cancellationToken);

    public async Task<string?> CheckGatewayAsync(CancellationToken cancellationToken = default)
    {
        if (await _gateway.LoadStateAsync(Site, cancellationToken) == null)
            return "Not set up yet.";
        try
        {
            return await _gateway.VerifyAsync(Site, cancellationToken);
        }
        catch (Exception ex) when (ex is NetworkOptimizer.UniFi.UniFiApiErrorException or InvalidOperationException or HttpRequestException)
        {
            return ex.Message;
        }
    }

    public async Task<CloudflareList> SetUpCloudflareAsync(string? token, string accountId, CancellationToken cancellationToken = default)
    {
        accountId = accountId.Trim();
        if (string.IsNullOrEmpty(accountId))
            throw new ArgumentException("Enter the Cloudflare Account ID.");
        var useToken = string.IsNullOrWhiteSpace(token)
            ? await _settings.GetDecryptedSettingAsync(Key(BlockingSettingKeys.CloudflareToken), cancellationToken)
            : token.Trim();
        if (string.IsNullOrEmpty(useToken))
            throw new ArgumentException("Enter a Cloudflare API token.");

        var listId = await _settings.GetSettingAsync(Key(BlockingSettingKeys.CloudflareListId), cancellationToken);
        var client = new CloudflareListClient(_httpClientFactory.CreateClient("Cloudflare"));
        var list = await client.EnsureListAsync(useToken, accountId, listId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(token))
            await _settings.SaveSettingAsync(Key(BlockingSettingKeys.CloudflareToken), _credentials.Encrypt(useToken), cancellationToken);
        await _settings.SaveSettingAsync(Key(BlockingSettingKeys.CloudflareAccountId), accountId, cancellationToken);
        await _settings.SaveSettingAsync(Key(BlockingSettingKeys.CloudflareListId), list.Id, cancellationToken);
        _blocking.Trigger();
        return list;
    }

    public async Task<CloudflareBlockingStatus> GetCloudflareStatusAsync(CancellationToken cancellationToken = default)
    {
        var token = await _settings.GetDecryptedSettingAsync(Key(BlockingSettingKeys.CloudflareToken), cancellationToken);
        var account = await _settings.GetSettingAsync(Key(BlockingSettingKeys.CloudflareAccountId), cancellationToken);
        var listId = await _settings.GetSettingAsync(Key(BlockingSettingKeys.CloudflareListId), cancellationToken);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(account) || string.IsNullOrEmpty(listId))
            return new CloudflareBlockingStatus(!string.IsNullOrEmpty(token), account, null, null);
        try
        {
            var list = await new CloudflareListClient(_httpClientFactory.CreateClient("Cloudflare"))
                .GetListAsync(token, account, listId, cancellationToken);
            return new CloudflareBlockingStatus(true, account, list, null);
        }
        catch (CloudflareException ex)
        {
            return new CloudflareBlockingStatus(true, account, null, ex.Message);
        }
    }
}
