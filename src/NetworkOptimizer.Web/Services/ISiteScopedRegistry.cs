namespace NetworkOptimizer.Web.Services;

/// <summary>
/// A registry that keeps one long-lived instance per site slug.
///
/// Removing a site has to empty every one of them. The instances capture that site's console
/// connection, gateway SSH session, InfluxDB client and database path when they are created, so a
/// registry that keeps its entry hands the dead objects of a removed site to whatever is created
/// under the same slug next - which is exactly what a re-created test site is. Upstream path
/// discovery failed that way: the tracer still held the removed site's console client, which
/// answered every device request with nothing.
///
/// Eviction and teardown are separate on purpose. The instances hold each other - the ISP Health
/// service holds the site's InfluxDB client, the tracer holds its connection and SSH services - so
/// disposing one while another registry still hands out its holder turns a stale object into an
/// ObjectDisposedException on the next background pass. Every registry evicts first; only then does
/// the caller run the teardowns.
/// </summary>
public interface ISiteScopedRegistry
{
    /// <summary>
    /// Drops the site's instance from this registry, returning a callback that tears it down, or
    /// null when there was nothing registered or nothing to tear down. The caller runs the
    /// callbacks only after every registry has evicted the site.
    /// </summary>
    Func<ValueTask>? EvictSite(string slug);
}

/// <summary>
/// Registers a per-site registry as both its own type and <see cref="ISiteScopedRegistry"/>, so
/// site removal sweeps it without anyone having to remember to add it to a list. Registering the
/// singleton by hand and forgetting the second line is how a registry ends up leaking removed
/// sites, so always use this.
/// </summary>
public static class SiteScopedRegistryRegistration
{
    public static IServiceCollection AddSiteScopedRegistry<T>(this IServiceCollection services)
        where T : class, ISiteScopedRegistry
    {
        services.AddSingleton<T>();
        services.AddSingleton<ISiteScopedRegistry>(sp => sp.GetRequiredService<T>());
        return services;
    }
}
