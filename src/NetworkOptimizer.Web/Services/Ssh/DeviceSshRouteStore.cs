using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>Which devices take the Gateway SSH credentials, by normalized MAC.</summary>
public interface IDeviceSshRouteStore
{
    Task<bool> UsesGatewayCredentialsAsync(string mac, CancellationToken ct = default);

    Task SetUsesGatewayCredentialsAsync(string mac, bool usesGatewayCredentials, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IDeviceSshRouteStore"/> over the site's <c>DeviceSshRoutes</c> table, read once and
/// then served from memory. Writes go to the database first, so a failed write never leaves the
/// cache claiming a route the next restart will not see.
/// </summary>
public sealed class DbDeviceSshRouteStore : IDeviceSshRouteStore
{
    private readonly Func<IServiceScope> _createSiteScope;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private ConcurrentDictionary<string, byte>? _macs;

    /// <param name="createSiteScope">A scope whose DbContext is this site's database.</param>
    public DbDeviceSshRouteStore(Func<IServiceScope> createSiteScope)
    {
        _createSiteScope = createSiteScope;
    }

    public async Task<bool> UsesGatewayCredentialsAsync(string mac, CancellationToken ct = default)
        => (await LoadAsync(ct)).ContainsKey(mac);

    public async Task SetUsesGatewayCredentialsAsync(string mac, bool usesGatewayCredentials, CancellationToken ct = default)
    {
        var macs = await LoadAsync(ct);

        using (var scope = _createSiteScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var row = await db.DeviceSshRoutes.FirstOrDefaultAsync(r => r.DeviceMac == mac, ct);
            if (usesGatewayCredentials && row == null)
                db.DeviceSshRoutes.Add(new DeviceSshRoute { DeviceMac = mac, UpdatedAt = DateTime.UtcNow });
            else if (!usesGatewayCredentials && row != null)
                db.DeviceSshRoutes.Remove(row);
            await db.SaveChangesAsync(ct);
        }

        if (usesGatewayCredentials) macs[mac] = 0;
        else macs.TryRemove(mac, out _);
    }

    private async Task<ConcurrentDictionary<string, byte>> LoadAsync(CancellationToken ct)
    {
        if (_macs != null) return _macs;

        await _loadGate.WaitAsync(ct);
        try
        {
            if (_macs != null) return _macs;

            using var scope = _createSiteScope();
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var rows = await db.DeviceSshRoutes.AsNoTracking().Select(r => r.DeviceMac).ToListAsync(ct);
            _macs = new ConcurrentDictionary<string, byte>(rows.Select(m => KeyValuePair.Create(m, (byte)0)));
            return _macs;
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
