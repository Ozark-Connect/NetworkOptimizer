using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The site's latency targets by public address, for cutting them out of its public block lists.
/// Paused targets count, so resuming one never meets a block; retired and hidden ones do not.
/// Hostnames are resolved and kept for <see cref="ResolveEvery"/>, failures included.
/// </summary>
public sealed class LatencyTargetAddressProvider(
    SiteDbContextFactory siteDb,
    SiteContextService siteContext,
    ILogger<LatencyTargetAddressProvider> logger) : IProbedAddressProvider
{
    private static readonly TimeSpan ResolveEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);
    private static readonly ConcurrentDictionary<string, (IPAddress[] Addresses, DateTime At)> Resolved =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<IReadOnlyList<IPAddress>> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = siteDb.CreateForSite(siteContext.Slug, siteContext.IsDefault);
        var targets = await db.MonitoringTargets.AsNoTracking()
            .Where(t => t.RetiredAt == null && t.HiddenAt == null && t.IsLocal != true)
            .Select(t => t.Address)
            .Distinct()
            .ToListAsync(cancellationToken);

        var addresses = new HashSet<IPAddress>();
        foreach (var target in targets)
        {
            foreach (var address in await ResolveAsync(target.Trim(), cancellationToken))
            {
                var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
                if (NetworkUtilities.IsPublicIpAddress(ip)) addresses.Add(ip);
            }
        }
        return addresses.ToList();
    }

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        var now = DateTime.UtcNow;
        if (Resolved.TryGetValue(host, out var cached) && now - cached.At < ResolveEvery) return cached.Addresses;
        IPAddress[] addresses;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ResolveTimeout);
            addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
        }
        catch (Exception ex) when ((ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
                                   && !ct.IsCancellationRequested)
        {
            logger.LogDebug("Could not resolve latency target {Host} for the public block lists: {Error}", host, ex.Message);
            addresses = cached.Addresses ?? [];
        }
        Resolved[host] = (addresses, now);
        return addresses;
    }
}
