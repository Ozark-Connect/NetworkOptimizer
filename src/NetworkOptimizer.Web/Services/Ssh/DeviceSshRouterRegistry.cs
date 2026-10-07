using System.Collections.Concurrent;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Owns one <see cref="DeviceSshRouter"/> per site, created on first use. Every feature that SSHes
/// into a UniFi device by its discovered identity (AP Agent, Health Checks, the reboot reason probe,
/// the LAN speed test) gets its route here, so the credential rule lives in one place.
/// </summary>
public class DeviceSshRouterRegistry : ISiteScopedRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, DeviceSshRouter> _instances = new();

    public DeviceSshRouterRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The router for a site, created on first use.</summary>
    public DeviceSshRouter GetFor(string slug) => _instances.GetOrAdd(slug, Create);

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _instances.TryRemove(slug, out _);
        return null;
    }

    private DeviceSshRouter Create(string slug)
    {
        var sp = _serviceProvider;
        var credentials = sp.GetRequiredService<ICredentialProtectionService>();

        IServiceScope CreateSiteScope()
        {
            var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(slug);
            return scope;
        }

        async Task<IReadOnlyList<DiscoveredDevice>> Devices(CancellationToken ct) =>
            await sp.GetRequiredService<SiteConnectionRegistry>().GetFor(slug).GetDiscoveredDevicesAsync(ct);

        async Task<SshConnectionInfo?> Prepare(SshConnectionInfo connection)
        {
            using (var scope = CreateSiteScope())
            {
                await StoredSshKeyReader.AttachAsync(scope.ServiceProvider, connection);
            }
            if (!connection.HasCredentials) return null;

            (connection.Host, connection.Port) = await sp.GetRequiredService<SiteTunnelRouting>()
                .RouteAsync(slug, connection.Host, connection.Port);
            return connection;
        }

        return new DeviceSshRouter(
            sp.GetRequiredService<UniFiSshRegistry>().GetFor(slug),
            sp.GetRequiredService<GatewaySshRegistry>().GetFor(slug),
            new DbDeviceSshRouteStore(CreateSiteScope),
            Devices,
            Prepare,
            credentials.Decrypt,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<DeviceSshRouter>());
    }
}
