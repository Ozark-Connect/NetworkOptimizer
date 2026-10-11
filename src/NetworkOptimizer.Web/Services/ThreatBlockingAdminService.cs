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

    /// <summary>Saves the token (when given) and account, then finds or creates the list.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ThreatBlockingTargetSetUp, Category = AuditCategories.Settings, TargetType = "cloudflare")]
    Task<CloudflareList> SetUpCloudflareAsync(string? token, string accountId, CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    Task<CloudflareBlockingStatus> GetCloudflareStatusAsync(CancellationToken cancellationToken = default);
}

public sealed class ThreatBlockingAdminService(ThreatBlockingOperations ops, SiteContextService site) : IThreatBlockingAdmin
{
    private string Slug => site.Slug;

    public Task<BlockingOverview> GetOverviewAsync(CancellationToken ct = default) => ops.GetOverviewAsync(Slug, ct);
    public Task SaveSettingsAsync(BlockingSettings settings, CancellationToken ct = default) => ops.SaveSettingsAsync(Slug, settings, ct);
    public Task<RecommendedResult> TurnOnRecommendedAsync(CancellationToken ct = default) => ops.TurnOnRecommendedAsync(Slug, ct);
    public Task<bool> ReleaseAsync(int blockId, string releasedBy, CancellationToken ct = default) => ops.ReleaseAsync(Slug, blockId, releasedBy, ct);
    public Task<ThreatBlock> BlockAsync(string ip, int minutes, string blockedBy, CancellationToken ct = default) => ops.BlockAsync(Slug, ip, minutes, blockedBy, ct);
    public Task<ThreatBlock?> FindActiveBlockAsync(string ip, CancellationToken ct = default) => ops.FindActiveBlockAsync(Slug, ip, ct);
    public Task<GatewaySetUpResult> SetUpGatewayAsync(CancellationToken ct = default) => ops.SetUpGatewayAsync(Slug, ct);
    public Task RemoveGatewayAsync(CancellationToken ct = default) => ops.RemoveGatewayAsync(Slug, ct);
    public Task<string?> CheckGatewayAsync(CancellationToken ct = default) => ops.CheckGatewayAsync(Slug, ct);
    public Task<CloudflareList> SetUpCloudflareAsync(string? token, string accountId, CancellationToken ct = default) => ops.SetUpCloudflareAsync(Slug, token, accountId, ct);
    public Task<CloudflareBlockingStatus> GetCloudflareStatusAsync(CancellationToken ct = default) => ops.GetCloudflareStatusAsync(Slug, ct);
}

/// <summary>One site's Automatic Blocking at a glance, for its row in Settings - Multi-Site.</summary>
public sealed record BlockingSiteSummary(BlockingMode Mode, bool Share, bool GatewaySetUp, int ActiveBlocks);

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
}

public sealed class ThreatBlockingSiteAdminService(ThreatBlockingOperations ops) : IThreatBlockingSiteAdmin
{
    public async Task<BlockingSiteSummary> GetSummaryAsync(string siteSlug, CancellationToken ct = default)
    {
        var overview = await ops.GetOverviewAsync(siteSlug, ct);
        return new BlockingSiteSummary(overview.Settings.Mode, overview.Settings.Share,
            await ops.IsGatewaySetUpAsync(siteSlug, ct), overview.Active.Count(b => b.Enforced));
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
}
