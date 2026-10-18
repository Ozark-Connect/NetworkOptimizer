using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.WanFlood;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.WanFlood;

/// <summary>An episode as the Threat Intelligence card lists it.</summary>
public sealed record WanFloodIncidentView(string IncidentId, string Kind, string Wan, DateTime StartedAt, DateTime? EndedAt,
    int PeakUnsolicitedPps, int PeakSoftirqPercent, int PeakConntrackPercent, bool TableFull, string Mode,
    IReadOnlyList<WanFloodDropView> Actions);

/// <summary>
/// WAN Flood Protection for the site in context. Reads are Viewer (the card is on Threat Intelligence);
/// every write is Site Admin and audited. What the gateway agent does on its own is audited by
/// <see cref="WanFloodService"/> as Network Optimizer.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IWanFloodAdmin
{
    [RequireRole(Roles.Viewer)]
    Task<WanFloodOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<WanFloodIncidentView>> GetIncidentsAsync(int days, CancellationToken cancellationToken = default);

    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "wan_flood")]
    Task SaveSettingsAsync(WanFloodSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Ends a drop or limit now; the agent does not re-add it for one time to live.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WanFloodDropReleased, Category = AuditCategories.Settings, TargetType = "wan_flood_drop")]
    Task ReleaseAsync(WanFloodDropView drop, CancellationToken cancellationToken = default);
}

public sealed class WanFloodAdminService(WanFloodService service, IThreatSettingsAccessor settings,
    IWanFloodIncidentRepository incidents, SiteContextService site, NetworkOptimizerDbContext db,
    Monitoring.MonitoringPathView paths) : IWanFloodAdmin
{
    private string Slug => site.Slug;
    private string? SiteKey => Slug == SiteManagementService.DefaultSiteSlug ? null : Slug;

    public async Task<WanFloodOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var current = await WanFloodSettings.LoadAsync(k => settings.GetSettingAsync(k, ct), SiteKey);
        var (agent, capable, status) = service.Describe(Slug);
        var (labels, down) = await WanNamesAsync(ct);
        return new WanFloodOverview(current, agent, capable, status) { WanLabels = labels, WansDown = down };
    }

    // The agent reports WANs by interface. The WAN profiles map an interface to its WAN key, and the gateway's WAN
    // list gives that key's name, labelled the way the rest of the app labels a WAN.
    private async Task<(Dictionary<string, string> Labels, HashSet<string> Down)> WanNamesAsync(CancellationToken ct)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var down = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var profiles = await db.WanProfiles.AsNoTracking()
                .Where(p => p.DataPathInterface != null && p.DataPathInterface != "")
                .Select(p => new { p.DataPathInterface, p.WanNetworkgroup })
                .ToListAsync(ct);
            var wans = await paths.GetWansAsync(ct);
            foreach (var p in profiles)
            {
                var key = GatewayWanHelper.WanInterfaceKeyFromKey(p.WanNetworkgroup);
                var index = GatewayWanHelper.WanIndexFromKey(key);
                var wan = wans.FirstOrDefault(w => string.Equals(w.WanInterface, key, StringComparison.OrdinalIgnoreCase));
                labels[p.DataPathInterface!] = GatewayWanHelper.FormatWanLabelInProse(
                    GatewayWanHelper.FormatWanLabel(wan?.FriendlyName, index, null, null), index);
                if (wan is { Up: false }) down.Add(p.DataPathInterface!);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Names are a nicety: without them the card shows interface names.
        }
        return (labels, down);
    }

    public async Task<IReadOnlyList<WanFloodIncidentView>> GetIncidentsAsync(int days, CancellationToken ct = default)
    {
        var rows = await incidents.GetSinceAsync(DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 90)), 200, ct);
        return rows.Select(r => new WanFloodIncidentView(r.IncidentId, r.Kind, r.Wan, r.StartedAt, r.EndedAt,
            r.PeakUnsolicitedPps, r.PeakSoftirqPercent, r.PeakConntrackPercent, r.TableFull, r.Mode,
            System.Text.Json.JsonSerializer.Deserialize<List<WanFloodDropView>>(r.ActionsJson) ?? [])).ToList();
    }

    public async Task SaveSettingsAsync(WanFloodSettings value, CancellationToken ct = default)
    {
        var s = value.Clamped();
        async Task Set(string key, string v) => await settings.SaveSettingAsync(WanFloodSettingKeys.ForSite(key, SiteKey), v, ct);
        await Set(WanFloodSettingKeys.Mode, s.Mode.ToString());
        await Set(WanFloodSettingKeys.ReactiveDrop, s.ReactiveDrop.ToString());
        await Set(WanFloodSettingKeys.UdpPerSource, s.UdpPerSource.ToString());
        await Set(WanFloodSettingKeys.UdpBurst, s.UdpBurst.ToString());
        await Set(WanFloodSettingKeys.SynPerSource, s.SynPerSource.ToString());
        await Set(WanFloodSettingKeys.SynBurst, s.SynBurst.ToString());
        await Set(WanFloodSettingKeys.ForwardedPort, s.ForwardedPort.ToString());
        await Set(WanFloodSettingKeys.ForwardedBurst, s.ForwardedBurst.ToString());
        await Set(WanFloodSettingKeys.ForwardedCapped, s.ForwardedCapped.ToString());
        await Set(WanFloodSettingKeys.DropTtl, s.DropTtlSeconds.ToString());
        await Set(WanFloodSettingKeys.MaxDropTtl, s.MaxDropTtlSeconds.ToString());
        await Set(WanFloodSettingKeys.NeverDrop, string.Join("\n", s.NeverDropEntries()));
        await Set(WanFloodSettingKeys.TestDrop, s.TestDrop.ToString());
        service.Trigger();
    }

    public Task ReleaseAsync(WanFloodDropView drop, CancellationToken ct = default)
    {
        service.Release(Slug, new WanFloodRelease
        {
            Wan = drop.Wan,
            Protocol = drop.Protocol,
            Source = WanFloodService.ReleaseSource(drop.Kind, drop.Source, drop.Destination),
            Port = drop.Port,
            Kind = drop.Kind,
        });
        return Task.CompletedTask;
    }
}
