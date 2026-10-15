using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.Threats.Waf;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>Where Automatic Blocking enforces its decisions.</summary>
public enum BlockTargetKind
{
    Gateway = 0,
    Waf = 1,
    Cloudflare = 2
}

/// <summary>The outcome of one sync to a target.</summary>
public sealed record BlockTargetResult(bool Ok, int Count, string? Error)
{
    public static BlockTargetResult Success(int count) => new(true, count, null);
    public static BlockTargetResult Failure(string error) => new(false, 0, error);
}

/// <summary>
/// A place a block list is enforced. Each sync sends the full desired set, so a missed or failed
/// sync heals on the next one.
/// </summary>
public interface IBlockTarget
{
    BlockTargetKind Kind { get; }

    /// <summary>
    /// True when the target keeps its list in memory and must be re-sent every cycle (the WAF
    /// loses its list on restart). Others are only sent when the set changes, or hourly.
    /// </summary>
    bool SyncEveryCycle { get; }

    Task<BlockTargetResult> SyncAsync(IReadOnlyList<ThreatBlock> desired, CancellationToken cancellationToken);
}

/// <summary>A target that also enforces the public block lists. Cloudflare never does: its edge has its own data.</summary>
public interface IFeedBlockTarget
{
    /// <summary>The list entries (IPs and CIDR ranges) to send with the next <see cref="IBlockTarget.SyncAsync"/>.</summary>
    IReadOnlyList<string> Feeds { get; set; }
}

/// <summary>
/// A site's netopt-waf ban list. Covers Cloudflare-proxied hosts, which a gateway block cannot. The
/// client reaches the site's WAF directly or through its agent (<see cref="ISiteHttpClientProvider"/>).
/// </summary>
public sealed class WafBlockTarget(WafClient client, IThreatSettingsAccessor settings, string? site) : IBlockTarget, IFeedBlockTarget
{
    // List entries outlive a missed sync by this much; the WAF is re-sent every minute.
    private static readonly TimeSpan FeedTtl = TimeSpan.FromHours(2);

    public BlockTargetKind Kind => BlockTargetKind.Waf;
    public bool SyncEveryCycle => true;
    public IReadOnlyList<string> Feeds { get; set; } = [];

    public async Task<BlockTargetResult> SyncAsync(IReadOnlyList<ThreatBlock> desired, CancellationToken cancellationToken)
    {
        var url = await settings.GetSettingAsync(WafSettingKeys.ForSite(WafSettingKeys.Url, site), cancellationToken);
        var token = await settings.GetDecryptedSettingAsync(WafSettingKeys.ForSite(WafSettingKeys.Token, site), cancellationToken);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            return BlockTargetResult.Failure("Connect the WAF in Settings - Security & Alerts - Web Application Firewall first.");
        try
        {
            var feedExpiry = DateTimeOffset.UtcNow + FeedTtl;
            var own = desired.Select(b => b.SourceIp).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bans = desired.Select(b => new WafBan(b.SourceIp, new DateTimeOffset(DateTime.SpecifyKind(b.ExpiresAt, DateTimeKind.Utc))))
                .Concat(Feeds.Where(f => !own.Contains(f)).Select(f => new WafBan(f, feedExpiry)))
                .ToList();
            return BlockTargetResult.Success(await client.PutBansAsync(url, token, bans, cancellationToken));
        }
        catch (WafClientException ex)
        {
            return BlockTargetResult.Failure(ex.Message);
        }
    }
}

/// <summary>
/// Gateway enforcement through a site's UniFi console: an address group (one per IP version) that
/// zone-based Block policies reference.
/// </summary>
public interface IGatewayBlocklistWriter
{
    /// <summary>Replaces the site's group members with <paramref name="ips"/> (null site = default).</summary>
    Task<BlockTargetResult> SyncMembersAsync(string? site, IReadOnlyList<string> ips, CancellationToken cancellationToken);

    /// <summary>Replaces the site's public block list groups with <paramref name="entries"/> (IPs and CIDR ranges).</summary>
    Task<BlockTargetResult> SyncFeedsAsync(string? site, IReadOnlyList<string> entries, CancellationToken cancellationToken);
}

/// <summary>A site's gateway, through <see cref="IGatewayBlocklistWriter"/>.</summary>
public sealed class GatewayBlockTarget(IGatewayBlocklistWriter writer, string? site) : IBlockTarget
{
    public BlockTargetKind Kind => BlockTargetKind.Gateway;
    public bool SyncEveryCycle => false;

    public Task<BlockTargetResult> SyncAsync(IReadOnlyList<ThreatBlock> desired, CancellationToken cancellationToken) =>
        writer.SyncMembersAsync(site, desired.Select(b => b.SourceIp).ToList(), cancellationToken);
}
