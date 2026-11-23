using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Owns one <see cref="LanHealthEngine"/> and its alert evaluator per site. The engine ticks from the
/// site's Change Control engine loop, before Site Health, so the hour Site Health scores already has
/// its LAN row.
/// </summary>
public sealed class LanHealthRegistry : ISiteScopedRegistry
{
    private readonly IServiceProvider _sp;
    private readonly ConcurrentDictionary<string, LanHealthEngine> _engines = new(StringComparer.OrdinalIgnoreCase);

    public LanHealthRegistry(IServiceProvider sp)
    {
        _sp = sp;
    }

    /// <summary>The site's engine, created on first use.</summary>
    public LanHealthEngine GetFor(string slug) => _engines.GetOrAdd(slug, Create);

    private LanHealthEngine Create(string slug)
    {
        var isDefault = string.Equals(slug, SiteManagementService.DefaultSiteSlug, StringComparison.OrdinalIgnoreCase);
        var mainFactory = _sp.GetRequiredService<IDbContextFactory<NetworkOptimizerDbContext>>();
        var siteFactory = _sp.GetRequiredService<SiteDbContextFactory>();
        var loggers = _sp.GetRequiredService<ILoggerFactory>();
        var engine = new LanHealthEngine(slug,
            async ct => isDefault ? await mainFactory.CreateDbContextAsync(ct) : siteFactory.CreateForSite(slug, isDefault: false),
            () => _sp.GetRequiredService<MonitoringInfluxRegistry>().GetFor(slug),
            () => _sp.GetRequiredService<SiteConnectionRegistry>().GetFor(slug),
            loggers.CreateLogger($"LanHealth[{slug}]"));
        engine.NotificationClock = _sp.GetService<NetworkOptimizer.Alerts.Interfaces.INotificationTimeZoneResolver>();
        var alerts = new LanHealthAlertEvaluator(_sp.GetService<NetworkOptimizer.Alerts.Events.IAlertEventBus>(),
            loggers.CreateLogger($"LanHealthAlerts[{slug}]"), slug, _sp.GetService<IServiceScopeFactory>());
        var suppression = _sp.GetService<Firmware.RolloutSuppressionRegistry>();
        engine.Refreshed += report =>
        {
            var now = DateTime.UtcNow;
            var suppressed = report.Links.Where(l => suppression != null && Touches(slug, l, report, suppression, now))
                .Select(l => l.Link.Id).ToHashSet();
            _ = alerts.EvaluateAsync(report, suppressed, now);
        };
        return engine;
    }

    /// <summary>A link a Firmware Rollout wave touches: either end in the wave, or anything above it (it drops when that reboots).</summary>
    private static bool Touches(string slug, LinkHealth l, LanHealthReport report, Firmware.RolloutSuppressionRegistry s, DateTime now)
    {
        if (s.IsInRolloutWindow(slug, l.Link.Upstream.DeviceMac, now) || s.IsInRolloutWindow(slug, l.Link.Downstream.DeviceMac, now)) return true;
        var seen = new HashSet<string>();
        var mac = l.Link.Upstream.DeviceMac;
        while (seen.Add(mac))
        {
            var parent = report.Links.FirstOrDefault(x => x.Link.IsFabric && x.Link.Downstream.DeviceMac == mac);
            if (parent == null) break;
            if (s.IsInRolloutWindow(slug, parent.Link.Upstream.DeviceMac, now)) return true;
            mac = parent.Link.Upstream.DeviceMac;
        }
        return false;
    }

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _engines.TryRemove(slug, out _);
        return null;
    }
}
