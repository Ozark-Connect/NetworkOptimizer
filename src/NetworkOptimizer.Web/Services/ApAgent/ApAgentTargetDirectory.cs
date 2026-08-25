using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>One access point that has an AP Agent this server may talk to.</summary>
/// <param name="Mac">The access point's MAC, lower-case colon form.</param>
/// <param name="Host">Address to reach it on, before tunnel routing.</param>
/// <param name="Token">Bearer token, decrypted, or null when it would not decrypt.</param>
public sealed record ApAgentTarget(string Mac, string Host, string? Token, string? Name);

/// <summary>
/// Which access points on a site have an AP Agent, cached per site.
///
/// Two callers need the same answer at very different rates: the 30 s telemetry collector and the
/// sub-second live poll behind Client Performance. One directory keeps them from drifting apart,
/// so an access point can never be enrolled for one path and absent from the other.
/// </summary>
public sealed class ApAgentTargetDirectory : ISiteScopedRegistry
{
    /// <summary>The console device list changes rarely; re-reading it every pass would not.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _serviceProvider;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly ILogger<ApAgentTargetDirectory> _logger;
    private readonly ConcurrentDictionary<string, SiteCache> _sites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the directory.</summary>
    public ApAgentTargetDirectory(
        IServiceProvider serviceProvider,
        ICredentialProtectionService credentialProtection,
        ILogger<ApAgentTargetDirectory> logger)
    {
        _serviceProvider = serviceProvider;
        _credentialProtection = credentialProtection;
        _logger = logger;
    }

    /// <summary>Whether this site opted in to AP Agents at all. False means no agent request is made.</summary>
    public async Task<bool> IsSiteEnabledAsync(string siteSlug, CancellationToken ct = default)
    {
        var cache = _sites.GetOrAdd(siteSlug, _ => new SiteCache());
        if (DateTime.UtcNow - cache.EnabledAt < CacheTtl) return cache.Enabled;

        try
        {
            using var scope = CreateSiteScope(siteSlug);
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var setting = await db.SystemSettings.FindAsync(
                new object[] { ApAgentDeploymentService.SiteEnabledSettingKey }, ct);
            cache.Enabled = bool.TryParse(setting?.Value, out var enabled) && enabled;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent directory could not read the site setting (site {Site})", siteSlug);
            cache.Enabled = false;
        }

        cache.EnabledAt = DateTime.UtcNow;
        return cache.Enabled;
    }

    /// <summary>
    /// Every enrolled, enabled, online access point on the site. Empty when the site opted out, so
    /// a caller that iterates this makes no requests at all on a site without AP Agents.
    /// </summary>
    public async Task<IReadOnlyList<ApAgentTarget>> GetTargetsAsync(string siteSlug, CancellationToken ct = default)
    {
        var cache = _sites.GetOrAdd(siteSlug, _ => new SiteCache());
        if (DateTime.UtcNow - cache.TargetsAt < CacheTtl) return cache.Targets;

        try
        {
            var connection = _serviceProvider.GetRequiredService<SiteConnectionRegistry>().GetFor(siteSlug);
            var devices = await connection.GetDiscoveredDevicesAsync(ct);

            using var scope = CreateSiteScope(siteSlug);
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var records = await db.ApAgentDeployments.AsNoTracking().ToListAsync(ct);
            var byMac = records.ToDictionary(r => r.DeviceMac, StringComparer.OrdinalIgnoreCase);

            var targets = new List<ApAgentTarget>();
            foreach (var device in devices)
            {
                if (device.Type != DeviceType.AccessPoint) continue;
                if (string.IsNullOrEmpty(device.DisplayIpAddress)) continue;
                if (device.State != 1) continue;

                var mac = ApAgentWifiFieldMapper.NormalizeMac(device.Mac);
                if (!byMac.TryGetValue(mac, out var record) || !record.Enabled) continue;

                targets.Add(new ApAgentTarget(mac, device.DisplayIpAddress, ResolveToken(record), device.Name));
            }

            cache.Targets = targets;
            cache.TargetsAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent directory could not list access points (site {Site})", siteSlug);
        }

        return cache.Targets;
    }

    /// <summary>One access point's target, or null when it has no AP Agent this server may use.</summary>
    public async Task<ApAgentTarget?> FindAsync(string siteSlug, string? apMac, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apMac)) return null;
        var wanted = ApAgentWifiFieldMapper.NormalizeMac(apMac);
        var targets = await GetTargetsAsync(siteSlug, ct);
        return targets.FirstOrDefault(t => t.Mac == wanted);
    }

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _sites.TryRemove(slug, out _);
        return null;
    }

    /// <summary>A token that will not decrypt is left absent; the agent refuses the request and the console path keeps the access point.</summary>
    private string? ResolveToken(ApAgentDeployment record)
    {
        if (string.IsNullOrEmpty(record.Token)) return null;
        try
        {
            return _credentialProtection.Decrypt(record.Token);
        }
        catch
        {
            return null;
        }
    }

    private IServiceScope CreateSiteScope(string siteSlug)
    {
        var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteSlug);
        return scope;
    }

    private sealed class SiteCache
    {
        public bool Enabled;
        public DateTime EnabledAt = DateTime.MinValue;
        public IReadOnlyList<ApAgentTarget> Targets = Array.Empty<ApAgentTarget>();
        public DateTime TargetsAt = DateTime.MinValue;
    }
}
