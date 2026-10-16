using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Automatic Blocking for the site in context: each site has its own gateway, may have its own
/// Cloudflare account, and opts out of sharing blocks separately. Reads are Viewer (the Blocked
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

    /// <summary>Blocks a public address by hand, for <paramref name="minutes"/>. Works in Dry Run too.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ThreatSourceBlocked, Category = AuditCategories.Settings, TargetType = "threat_block")]
    Task<ThreatBlock> BlockAsync(string ip, int minutes, string blockedBy, CancellationToken cancellationToken = default);

    /// <summary>The active block on an address, or null. Viewer: the IP details show it.</summary>
    [RequireRole(Roles.Viewer)]
    Task<ThreatBlock?> FindActiveBlockAsync(string ip, CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ThreatBlockingTargetSetUp, Category = AuditCategories.Settings, TargetType = "gateway")]
    Task<GatewaySetUpResult> SetUpGatewayAsync(CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ThreatBlockingTargetRemoved, Category = AuditCategories.Settings, TargetType = "gateway")]
    Task RemoveGatewayAsync(CancellationToken cancellationToken = default);

    /// <summary>Null when the gateway objects are set up and intact, otherwise why not.</summary>
    [RequireRole(Roles.Admin)]
    Task<string?> CheckGatewayAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the On-Site Agent on the site's gateway currently holds its blocklist.</summary>
    [RequireRole(Roles.Admin)]
    Task<bool> IsGatewayAgentEnforcingAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the token (when given) and account, then finds or creates the list.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ThreatBlockingTargetSetUp, Category = AuditCategories.Settings, TargetType = "cloudflare")]
    Task<CloudflareList> SetUpCloudflareAsync(string? token, string accountId, CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    Task<CloudflareBlockingStatus> GetCloudflareStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Every public block list, whether this site uses it, and its last download.</summary>
    [RequireRole(Roles.Admin)]
    Task<IReadOnlyList<PublicListView>> GetPublicListsAsync(CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    Task<TrustedSourcesOverview> GetTrustedSourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>The Protection Setup checklist's state. Viewer: it is on the Threat Intelligence Overview.</summary>
    [RequireRole(Roles.Viewer)]
    Task<ProtectionStatus> GetProtectionStatusAsync(CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "threat_blocking")]
    Task SetProtectionSetupHiddenAsync(bool hidden, CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "trusted_sources")]
    Task SaveTrustedSourcesAsync(IReadOnlyList<string> trustedSiteSlugs, string extras, string neverBlock, bool uniFiGroup,
        bool uniFiCloudflare, CancellationToken cancellationToken = default);
}

public sealed class ThreatBlockingAdminService(ThreatBlockingOperations ops, SiteContextService site,
    Authorization.ISiteAccessFilter siteAccess) : IThreatBlockingAdmin
{
    private string Slug => site.Slug;

    public async Task<BlockingOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var overview = await ops.GetOverviewAsync(Slug, ct);
        if (await siteAccess.AuthorizedSlugsAsync() is not { } visible) return overview;
        // Blocks from sites the caller cannot see stay listed, without the site or the user who made them.
        return overview with
        {
            Shared = overview.Shared.Select(s => s.SiteSlug != null && visible.Contains(s.SiteSlug)
                ? s : new SharedBlock(null, s.Block.ForOtherSites())).ToList()
        };
    }
    public Task SaveSettingsAsync(BlockingSettings settings, CancellationToken ct = default) => ops.SaveSettingsAsync(Slug, settings, ct);
    public Task<RecommendedResult> TurnOnRecommendedAsync(CancellationToken ct = default) => ops.TurnOnRecommendedAsync(Slug, ct);
    public Task<bool> ReleaseAsync(int blockId, string releasedBy, CancellationToken ct = default) => ops.ReleaseAsync(Slug, blockId, releasedBy, ct);
    public Task<ThreatBlock> BlockAsync(string ip, int minutes, string blockedBy, CancellationToken ct = default) => ops.BlockAsync(Slug, ip, minutes, blockedBy, ct);
    public Task<ThreatBlock?> FindActiveBlockAsync(string ip, CancellationToken ct = default) => ops.FindActiveBlockAsync(Slug, ip, ct);
    public Task<GatewaySetUpResult> SetUpGatewayAsync(CancellationToken ct = default) => ops.SetUpGatewayAsync(Slug, ct);
    public Task RemoveGatewayAsync(CancellationToken ct = default) => ops.RemoveGatewayAsync(Slug, ct);
    public Task<string?> CheckGatewayAsync(CancellationToken ct = default) => ops.CheckGatewayAsync(Slug, ct);
    public Task<bool> IsGatewayAgentEnforcingAsync(CancellationToken ct = default) => Task.FromResult(ops.IsGatewayAgentEnforcing(Slug));
    public Task<CloudflareList> SetUpCloudflareAsync(string? token, string accountId, CancellationToken ct = default) => ops.SetUpCloudflareAsync(Slug, token, accountId, ct);
    public Task<CloudflareBlockingStatus> GetCloudflareStatusAsync(CancellationToken ct = default) => ops.GetCloudflareStatusAsync(Slug, ct);
    public Task<IReadOnlyList<PublicListView>> GetPublicListsAsync(CancellationToken ct = default) => ops.GetPublicListsAsync(Slug, ct);
    public async Task<TrustedSourcesOverview> GetTrustedSourcesAsync(CancellationToken ct = default) =>
        await ops.GetTrustedSourcesAsync(Slug, await siteAccess.AuthorizedSlugsAsync(), ct);
    public Task<ProtectionStatus> GetProtectionStatusAsync(CancellationToken ct = default) => ops.GetProtectionStatusAsync(Slug, ct);
    public Task SetProtectionSetupHiddenAsync(bool hidden, CancellationToken ct = default) => ops.SetProtectionSetupHiddenAsync(Slug, hidden, ct);
    public async Task SaveTrustedSourcesAsync(IReadOnlyList<string> trustedSiteSlugs, string extras, string neverBlock, bool uniFiGroup,
        bool uniFiCloudflare, CancellationToken ct = default) =>
        await ops.SaveTrustedSourcesAsync(Slug, trustedSiteSlugs, await siteAccess.AuthorizedSlugsAsync(), extras, neverBlock,
            uniFiGroup, uniFiCloudflare, ct);
}

/// <summary>
/// One site's Automatic Blocking at a glance, for its row in Settings - Multi-Site.
/// <see cref="ActiveBlocks"/> includes <see cref="SharedBlocks"/>, the sources other sites raised;
/// <see cref="FeedEntries"/> is null until the site's gateway has had a public list write.
/// </summary>
public sealed record BlockingSiteSummary(BlockingMode Mode, bool Share, bool GatewaySetUp, int ActiveBlocks, string? RecommendedNote,
    int SharedBlocks = 0, int? FeedEntries = null);

/// <summary>
/// Automatic Blocking for a named site, for Settings - Multi-Site, which lists every site rather than
/// the one in context. Each call is gated on the caller's role on THAT site and audited against it,
/// so "Turn On Recommended for All Sites" is one gated call per site the caller administers.
/// </summary>
[MutatingService]
public interface IThreatBlockingSiteAdmin
{
    [RequireSiteRole(SiteRole.SiteViewer)]
    Task<BlockingSiteSummary> GetSummaryAsync([SiteSlug] string siteSlug, CancellationToken cancellationToken = default);

    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "threat_blocking")]
    Task SetModeAsync([SiteSlug] string siteSlug, BlockingMode mode, CancellationToken cancellationToken = default);

    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "threat_blocking")]
    Task SetShareAsync([SiteSlug] string siteSlug, bool share, CancellationToken cancellationToken = default);

    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "threat_blocking")]
    Task<RecommendedResult> TurnOnRecommendedAsync([SiteSlug] string siteSlug, CancellationToken cancellationToken = default);

    /// <summary>The sites <paramref name="siteSlug"/> trusts, for its row in the Trusted Sources matrix.</summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    Task<IReadOnlyList<string>> GetTrustedSitesAsync([SiteSlug] string siteSlug, CancellationToken cancellationToken = default);

    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "trusted_sources")]
    Task SetTrustedSitesAsync([SiteSlug] string siteSlug, IReadOnlyList<string> trustedSiteSlugs, CancellationToken cancellationToken = default);

    /// <summary>
    /// The site's current public addresses (single IPs, not prefixes), for Network Tools' target list.
    /// Gated on that site, so a user only sees the sites they can open.
    /// </summary>
    [RequireSiteRole(SiteRole.SiteViewer)]
    Task<IReadOnlyList<string>> GetSiteAddressesAsync([SiteSlug] string siteSlug, CancellationToken cancellationToken = default);

    /// <summary>Forgets an address <paramref name="siteSlug"/> no longer reports. Gated on the site the address belongs to.</summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "trusted_sources")]
    Task<bool> RemoveTrustedAddressAsync([SiteSlug] string siteSlug, string address, CancellationToken cancellationToken = default);
}

public sealed class ThreatBlockingSiteAdminService(ThreatBlockingOperations ops, Authorization.ISiteAccessFilter siteAccess)
    : IThreatBlockingSiteAdmin
{
    public async Task<BlockingSiteSummary> GetSummaryAsync(string siteSlug, CancellationToken ct = default)
    {
        var overview = await ops.GetOverviewAsync(siteSlug, ct);
        // A source this site and a peer both raised is one source on the gateway.
        var own = overview.Active.Where(b => b.Enforced).Select(b => b.SourceIp).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shared = overview.Shared.Select(s => s.Block.SourceIp).Where(ip => !own.Contains(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new BlockingSiteSummary(overview.Settings.Mode, overview.Settings.Share,
            await ops.IsGatewaySetUpAsync(siteSlug, ct), own.Count + shared,
            PublicBlockLists.RecommendedNote(overview.Settings.Feeds),
            shared, ops.GatewayFeedEntries(siteSlug));
    }

    public async Task SetModeAsync(string siteSlug, BlockingMode mode, CancellationToken ct = default)
    {
        var current = await ops.GetSettingsAsync(siteSlug, ct);
        await ops.SaveSettingsAsync(siteSlug, current with { Mode = mode }, ct);
    }

    public async Task SetShareAsync(string siteSlug, bool share, CancellationToken ct = default)
    {
        var current = await ops.GetSettingsAsync(siteSlug, ct);
        await ops.SaveSettingsAsync(siteSlug, current with { Share = share }, ct);
    }

    public Task<RecommendedResult> TurnOnRecommendedAsync(string siteSlug, CancellationToken ct = default) =>
        ops.TurnOnRecommendedAsync(siteSlug, ct);

    public async Task<IReadOnlyList<string>> GetTrustedSitesAsync(string siteSlug, CancellationToken ct = default) =>
        (await ops.GetTrustedSourcesAsync(siteSlug, await siteAccess.AuthorizedSlugsAsync(), ct)).Sites
            .Where(s => s.Trusted).Select(s => s.Slug).ToList();

    public async Task SetTrustedSitesAsync(string siteSlug, IReadOnlyList<string> trustedSiteSlugs, CancellationToken ct = default) =>
        await ops.SaveTrustedSitesAsync(siteSlug, trustedSiteSlugs, await siteAccess.AuthorizedSlugsAsync(), ct);

    public Task<IReadOnlyList<string>> GetSiteAddressesAsync(string siteSlug, CancellationToken ct = default) =>
        ops.GetCurrentSiteAddressesAsync(siteSlug, ct);

    public Task<bool> RemoveTrustedAddressAsync(string siteSlug, string address, CancellationToken ct = default) =>
        ops.RemoveTrustedAddressAsync(siteSlug, address, ct);
}
