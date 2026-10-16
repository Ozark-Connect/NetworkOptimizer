using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// A block this site enforces because another site in its sharing group raised it.
/// <see cref="SiteSlug"/> is null when the caller cannot see that site.
/// </summary>
public sealed record SharedBlock(string? SiteSlug, ThreatBlock Block);

/// <summary>What the Blocked Sources card and the Automatic Blocking card show for one site.</summary>
public sealed record BlockingOverview(
    BlockingSettings Settings,
    IReadOnlyList<ThreatBlock> Active,
    IReadOnlyList<ThreatBlock> Recent,
    IReadOnlyList<SharedBlock> Shared,
    IReadOnlyList<BlockTargetStatus> Targets);

/// <summary>What Turn On Recommended enabled. <see cref="GatewayError"/> says why the gateway was left out.</summary>
public sealed record RecommendedResult(bool Gateway, string? GatewayError, bool Waf);

/// <summary>One site in a Trusted Sources view: whether it is trusted, and every address it has been seen at.</summary>
public sealed record TrustedSite(string Slug, bool Trusted, IReadOnlyList<TrustedAddress> Addresses);

/// <summary>A site's Trusted Sources, for the Settings card.</summary>
public sealed record TrustedSourcesOverview(
    IReadOnlyList<TrustedSite> Sites,
    string Extras,
    string NeverBlock,
    bool UniFiGroup,
    bool UniFiCloudflare,
    BlockTargetStatus? UniFiStatus,
    BlockTargetStatus? WafStatus);

/// <summary>A public block list as a site sees it: chosen or not, and its last download.</summary>
public sealed record PublicListView(PublicBlockList List, bool Enabled, PublicBlockListStatus Status, bool HasToken = false);

/// <summary>
/// Where a site stands on the recommended protection setup, for the Protection Setup checklist.
/// <see cref="Complete"/> needs Enforce, blocks reaching the gateway or the WAF, and the site's own
/// addresses known; Cloudflare and the UniFi groups are optional.
/// </summary>
public sealed record ProtectionStatus(
    bool EventsSeen,
    bool WafConnected,
    int SiteAddresses,
    int TrustedSites,
    int TrustedAddresses,
    int ExtraAddresses,
    BlockingMode Mode,
    int WouldBlock,
    bool GatewaySetUp,
    bool CloudflareSetUp,
    bool UniFiGroups,
    bool Hidden,
    bool WafTarget,
    string? RecommendedNote,
    string? WafMode = null,
    DateTimeOffset? WafModeSince = null)
{
    /// <summary>Whole days the WAF has run in detect mode, or 0 when it blocks or is unknown.</summary>
    public int WafDetectDays => WafMode == "detect" && WafModeSince is { } since ? (int)(DateTimeOffset.UtcNow - since).TotalDays : 0;

    /// <summary>Blocks reach somewhere: the gateway, or the WAF where a site has no gateway blocking.</summary>
    public bool BlockingTargetReady => GatewaySetUp || (WafConnected && WafTarget);

    public bool Complete => Mode == BlockingMode.Enforce && BlockingTargetReady && SiteAddresses > 0;
}

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
    private readonly PublicBlockListCache _lists;
    private readonly TrustedSourcesGroup _trustedGroup;
    private readonly NetworkOptimizer.Threats.ThreatCollectionService _collector;
    private readonly SiteConnectionRegistry? _connections;

    public ThreatBlockingOperations(
        IThreatSettingsAccessor settings,
        ThreatBlockingService blocking,
        GatewayBlocklist gateway,
        IBlockSharingScope sharing,
        IServiceScopeFactory scopeFactory,
        ICredentialProtectionService credentials,
        IHttpClientFactory httpClientFactory,
        PublicBlockListCache lists,
        TrustedSourcesGroup trustedGroup,
        NetworkOptimizer.Threats.ThreatCollectionService collector,
        NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? siteContext = null,
        SiteConnectionRegistry? connections = null)
    {
        _connections = connections;
        _collector = collector;
        _lists = lists;
        _trustedGroup = trustedGroup;
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

    /// <summary>Public list entries on the site's gateway, or null before the first write since startup.</summary>
    public int? GatewayFeedEntries(string slug) => _blocking.GatewayFeedEntries(Site(slug));

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

        // A Detection Only record whose source now has a real block is superseded by it.
        var active = blocks.Where(b => b.IsActive(now)).ToList();
        var enforced = active.Where(b => b.Enforced).Select(b => b.SourceIp).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new BlockingOverview(config,
            active.Where(b => b.Enforced || !enforced.Contains(b.SourceIp)).ToList(),
            blocks.Where(b => !b.IsActive(now)).ToList(),
            shared,
            _blocking.TargetStatus(site));
    }

    public async Task SaveSettingsAsync(string slug, BlockingSettings s, CancellationToken ct)
    {
        var before = await GetSettingsAsync(slug, ct);
        async Task Set(string key, string value) => await _settings.SaveSettingAsync(Key(slug, key), value, ct);
        await Set(BlockingSettingKeys.Mode, s.Mode.ToString());
        await Set(BlockingSettingKeys.WafEventThreshold, Math.Clamp(s.WafEventThreshold, BlockingSettings.MinThreshold, BlockingSettings.MaxThreshold).ToString());
        await Set(BlockingSettingKeys.WindowMinutes, Math.Clamp(s.WindowMinutes, 1, BlockingSettings.MaxWindowMinutes).ToString());
        await Set(BlockingSettingKeys.UsePatterns, s.UsePatterns.ToString());
        await Set(BlockingSettingKeys.BanMinutes, Math.Clamp(s.BanMinutes, 1, BlockingSettings.MaxBanMinutes).ToString());
        await Set(BlockingSettingKeys.MaxEntries, Math.Clamp(s.MaxEntries, 1, BlockingSettings.MaxMaxEntries).ToString());
        await Set(BlockingSettingKeys.Share, s.Share.ToString());
        await Set(BlockingSettingKeys.Feeds, string.Join(",", s.Feeds.Where(id => PublicBlockLists.Find(id) != null).Distinct()));
        await Set(BlockingSettingKeys.FeedsOutbound, s.FeedsOutbound.ToString());
        if (s.FeedsOutbound != before.FeedsOutbound && await IsGatewaySetUpAsync(slug, ct))
            await _gateway.SetFeedsOutboundAsync(Site(slug), s.FeedsOutbound, ct);
        await Set(BlockingSettingKeys.TargetGateway, s.TargetGateway.ToString());
        await Set(BlockingSettingKeys.TargetWaf, s.TargetWaf.ToString());
        await Set(BlockingSettingKeys.TargetCloudflare, s.TargetCloudflare.ToString());
        // Mode and sharing decide what this site gives the sites sharing its blocks; everything else
        // (lists, thresholds, targets) changes only this site's own set, so only its console is written.
        if (s.Mode != before.Mode || s.Share != before.Share)
            _blocking.TriggerConsoleSync();
        else
            _blocking.TriggerConsoleSyncFor(Site(slug));
    }

    public async Task<RecommendedResult> TurnOnRecommendedAsync(string slug, CancellationToken ct)
    {
        var current = await GetSettingsAsync(slug, ct);
        string? gatewayError = null;
        try
        {
            await _gateway.SetUpAsync(Site(slug), ct);
            if (current.FeedsOutbound)
                await _gateway.SetFeedsOutboundAsync(Site(slug), true, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
                                   or NetworkOptimizer.UniFi.UniFiApiErrorException or NetworkOptimizer.UniFi.UniFiPermissionException)
        {
            gatewayError = ex.Message;
        }
        var waf = await BlockingSettings.WafConnectedAsync(k => _settings.GetSettingAsync(k, ct), Site(slug));

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
        var allowlist = await _blocking.BuildAllowlistAsync(scope.ServiceProvider, site, settings, DateTime.UtcNow, ct);
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
        if (released) _blocking.TriggerConsoleSync();
        return released;
    }

    public async Task<GatewaySetUpResult> SetUpGatewayAsync(string slug, CancellationToken ct)
    {
        var result = await _gateway.SetUpAsync(Site(slug), ct);
        if ((await GetSettingsAsync(slug, ct)).FeedsOutbound)
            await _gateway.SetFeedsOutboundAsync(Site(slug), true, ct);
        _blocking.Trigger();
        return result;
    }

    public Task RemoveGatewayAsync(string slug, CancellationToken ct) => _gateway.RemoveAsync(Site(slug), ct);

    /// <summary>True when Set Up has run for the site (whether or not the objects are still intact).</summary>
    public async Task<bool> IsGatewaySetUpAsync(string slug, CancellationToken ct) =>
        await _gateway.LoadStateAsync(Site(slug), ct) != null;

    /// <summary>Whether the On-Site Agent on the site's gateway currently holds its blocklist.</summary>
    public bool IsGatewayAgentEnforcing(string slug) => _blocking.GatewayAgentEnforcing(Site(slug));

    /// <summary>Null when the gateway objects are set up and intact, otherwise why not.</summary>
    public async Task<string?> CheckGatewayAsync(string slug, CancellationToken ct)
    {
        if (!await IsGatewaySetUpAsync(slug, ct))
            return "Not set up yet.";
        // A configured console that is still (re)connecting, as after a restart, is not a missing one.
        if (_connections?.GetFor(slug) is { } connection && !await connection.WaitForConnectionAsync()
            && await connection.GetSettingsAsync() is { IsConfigured: true, HasCredentials: true })
            return "The UniFi Console connection is not up yet. Select Check once it is.";
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
        // Setting up the target enables it, so it does not depend on a separate Save of the card.
        await _settings.SaveSettingAsync(Key(slug, BlockingSettingKeys.TargetCloudflare), true.ToString(), ct);
        _blocking.Trigger();
        return list;
    }

    public async Task<ProtectionStatus> GetProtectionStatusAsync(string slug, CancellationToken ct)
    {
        var site = Site(slug);
        var settings = await GetSettingsAsync(slug, ct);
        var now = DateTime.UtcNow;
        bool eventsSeen;
        int wouldBlock;
        using (var scope = ScopeFor(site))
        {
            var repository = scope.ServiceProvider.GetRequiredService<IThreatRepository>();
            eventsSeen = (await repository.GetThreatSummaryAsync(now.AddDays(-7), now, ct)).TotalEvents > 0;
            wouldBlock = (await repository.GetBlocksAsync(now, now, ct)).Count(b => b.IsActive(now) && !b.Enforced);
        }
        var trusted = await TrustedSourcesSettings.ResolveSitesAsync(_settings, _sharing, site, ct);
        var trustedAddresses = 0;
        foreach (var s in trusted)
            trustedAddresses += (await TrustedAddressBook.LoadAsync(_settings, s, ct)).Count;
        return new ProtectionStatus(
            eventsSeen,
            await BlockingSettings.WafConnectedAsync(k => _settings.GetSettingAsync(k, ct), site),
            (await TrustedAddressBook.LoadAsync(_settings, site, ct)).Count,
            trusted.Count,
            trustedAddresses,
            BlockAllowlist.ParseList(await _settings.GetSettingAsync(Key(slug, TrustedSourcesKeys.Extras), ct)).Count(),
            settings.Mode,
            wouldBlock,
            await IsGatewaySetUpAsync(slug, ct),
            !string.IsNullOrEmpty(await _settings.GetSettingAsync(Key(slug, BlockingSettingKeys.CloudflareListId), ct)),
            await TrustedSourcesSettings.UniFiGroupEnabledAsync(_settings, site, ct),
            bool.TryParse(await _settings.GetSettingAsync(Key(slug, BlockingSettingKeys.SetupHidden), ct), out var hidden) && hidden,
            settings.TargetWaf,
            PublicBlockLists.RecommendedNote(settings.Feeds),
            _collector.WafStatusFor(slug)?.Mode,
            _collector.WafStatusFor(slug)?.ModeSince);
    }

    public Task SetProtectionSetupHiddenAsync(string slug, bool hidden, CancellationToken ct) =>
        _settings.SaveSettingAsync(Key(slug, BlockingSettingKeys.SetupHidden), hidden.ToString(), ct);

    /// <summary>Every public list with this site's choice and the last download.</summary>
    public async Task<IReadOnlyList<PublicListView>> GetPublicListsAsync(string slug, CancellationToken ct)
    {
        var chosen = (await GetSettingsAsync(slug, ct)).Feeds;
        var views = new List<PublicListView>();
        foreach (var l in PublicBlockLists.All)
        {
            var token = l.NeedsToken ? await _settings.GetDecryptedSettingAsync(Key(slug, BlockingSettingKeys.FeedToken(l.Id)), ct) : null;
            views.Add(new PublicListView(l, chosen.Contains(l.Id), l.NeedsToken ? _lists.Status(l.Id, token) : _lists.Status(l.Id),
                HasToken: !string.IsNullOrEmpty(token)));
        }
        return views;
    }

    /// <summary>Saves (or with an empty token, clears) a public list's API token for the site, encrypted.</summary>
    public async Task SavePublicListTokenAsync(string slug, string listId, string? token, CancellationToken ct)
    {
        if (PublicBlockLists.Find(listId) is not { NeedsToken: true })
            throw new ArgumentException("That public block list does not take a token.");
        var value = string.IsNullOrWhiteSpace(token) ? "" : _credentials.Encrypt(token.Trim());
        await _settings.SaveSettingAsync(Key(slug, BlockingSettingKeys.FeedToken(listId)), value, ct);
        _blocking.Trigger();
    }

    private string SlugOf(string? site) => site ?? _siteContext?.DefaultKey ?? "";

    /// <summary>
    /// A site's Trusted Sources, listing only <paramref name="visible"/> sites (null = every site) and
    /// always the site itself, so a Site Admin never sees another site's addresses.
    /// </summary>
    public async Task<TrustedSourcesOverview> GetTrustedSourcesAsync(string slug, IReadOnlySet<string>? visible, CancellationToken ct)
    {
        var site = Site(slug);
        var trusted = (await TrustedSourcesSettings.ResolveSitesAsync(_settings, _sharing, site, ct)).ToHashSet();
        var all = _siteContext == null ? [null] : (await _siteContext.GetSiteKeysAsync(ct)).Select(Site).ToList();
        var sites = new List<TrustedSite>();
        foreach (var s in all)
        {
            if (visible != null && s != site && !visible.Contains(SlugOf(s))) continue;
            sites.Add(new TrustedSite(SlugOf(s), trusted.Contains(s), await TrustedAddressBook.LoadAsync(_settings, s, ct)));
        }
        return new TrustedSourcesOverview(
            sites,
            await _settings.GetSettingAsync(Key(slug, TrustedSourcesKeys.Extras), ct) ?? "",
            await _settings.GetSettingAsync(Key(slug, BlockingSettingKeys.Allowlist), ct) ?? "",
            await TrustedSourcesSettings.UniFiGroupEnabledAsync(_settings, site, ct),
            await TrustedSourcesSettings.UniFiIncludesCloudflareAsync(_settings, site, ct),
            _blocking.TrustedSourcesStatus(site, BlockTargetKind.Gateway),
            _blocking.TrustedSourcesStatus(site, BlockTargetKind.Waf));
    }

    /// <summary>
    /// The sites a site trusts (always itself), its extra trusted addresses, its blocking exceptions,
    /// and whether it keeps the UniFi groups.
    /// </summary>
    public async Task SaveTrustedSourcesAsync(string slug, IReadOnlyList<string> trustedSlugs, IReadOnlySet<string>? visible,
        string extras, string neverBlock, bool uniFiGroup, bool uniFiCloudflare, CancellationToken ct)
    {
        var invalid = BlockAllowlist.Invalid(extras).Concat(BlockAllowlist.Invalid(neverBlock)).ToList();
        if (invalid.Count > 0)
            throw new ArgumentException($"Not an IP address or CIDR range: {string.Join(", ", invalid.Take(5))}");
        await SaveTrustedSitesAsync(slug, trustedSlugs, visible, ct);
        await _settings.SaveSettingAsync(Key(slug, TrustedSourcesKeys.Extras), string.Join("\n", BlockAllowlist.ParseList(extras)), ct);
        await _settings.SaveSettingAsync(Key(slug, BlockingSettingKeys.Allowlist), string.Join("\n", BlockAllowlist.ParseList(neverBlock)), ct);
        await _settings.SaveSettingAsync(Key(slug, TrustedSourcesKeys.UniFiGroup), uniFiGroup.ToString(), ct);
        await _settings.SaveSettingAsync(Key(slug, TrustedSourcesKeys.UniFiCloudflare), uniFiCloudflare.ToString(), ct);
        // Trusted Sources only change this site's own allowlist; other sites use its observed addresses.
        _blocking.TriggerConsoleSyncFor(Site(slug));
    }

    /// <summary>
    /// Sets the sites a site trusts, as far as the caller can see (<paramref name="visible"/>, null =
    /// every site): stored trust in sites outside it is kept, and the caller cannot add one.
    /// </summary>
    public async Task SaveTrustedSitesAsync(string slug, IReadOnlyList<string> trustedSlugs, IReadOnlySet<string>? visible,
        CancellationToken ct)
    {
        var site = Site(slug);
        IEnumerable<string> chosen = trustedSlugs;
        if (visible != null)
        {
            var hidden = (await TrustedSourcesSettings.ResolveSitesAsync(_settings, _sharing, site, ct))
                .Select(SlugOf).Where(s => !visible.Contains(s));
            chosen = trustedSlugs.Where(visible.Contains).Concat(hidden);
        }
        await TrustedSourcesSettings.SaveSitesAsync(_settings, site, chosen.Select(Site).Append(site), ct);
        _blocking.TriggerConsoleSyncFor(site);
    }

    /// <summary>A site's current public addresses, single IPs only (prefixes cannot be probed).</summary>
    public async Task<IReadOnlyList<string>> GetCurrentSiteAddressesAsync(string slug, CancellationToken ct) =>
        (await TrustedAddressBook.LoadAsync(_settings, Site(slug), ct))
            .Where(a => a.ReplacedAt == null && !a.Address.Contains('/'))
            .Select(a => a.Address)
            .ToList();

    /// <summary>
    /// Forgets an address a site no longer reports. Refused for a current one: the next read would
    /// add it straight back.
    /// </summary>
    public async Task<bool> RemoveTrustedAddressAsync(string slug, string address, CancellationToken ct)
    {
        var site = Site(slug);
        var list = await TrustedAddressBook.LoadAsync(_settings, site, ct);
        var entry = list.FirstOrDefault(a => string.Equals(a.Address, address, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return false;
        if (entry.ReplacedAt == null)
            throw new ArgumentException($"{address} is this site's current address. Only addresses it no longer reports can be removed.");
        await TrustedAddressBook.SaveAsync(_settings, site, list.Where(a => a != entry).ToList(), ct);
        _blocking.Trigger();
        return true;
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
