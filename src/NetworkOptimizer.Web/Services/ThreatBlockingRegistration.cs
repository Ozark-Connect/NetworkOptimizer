namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The singletons of Threat Intelligence collection, Automatic Blocking, and gateway agent enforcement.
/// They reach each other through factory registrations, which the container cannot check for cycles, so
/// they are registered here, where a test builds them the way the app does.
/// </summary>
public static class ThreatBlockingRegistration
{
    public static IServiceCollection AddThreatBlockingCore(this IServiceCollection services)
    {
        services.AddSingleton<NetworkOptimizer.Threats.ThreatCollectionService>();
        services.AddHostedService(sp => sp.GetRequiredService<NetworkOptimizer.Threats.ThreatCollectionService>());
        services.AddSingleton<NetworkOptimizer.Threats.Blocking.ThreatBlockingService>();
        services.AddHostedService(sp => sp.GetRequiredService<NetworkOptimizer.Threats.Blocking.ThreatBlockingService>());
        services.AddSingleton<NetworkOptimizer.Threats.Blocking.IPublicListLookup>(sp =>
            sp.GetRequiredService<NetworkOptimizer.Threats.Blocking.ThreatBlockingService>());
        services.AddSingleton<GatewayAgentBlocklist>();
        services.AddSingleton<NetworkOptimizer.Threats.Blocking.IGatewayAgentBlocklist>(sp => sp.GetRequiredService<GatewayAgentBlocklist>());
        return services;
    }
}
