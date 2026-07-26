using Castle.DynamicProxy;

namespace NetworkOptimizer.Web.Services.Gates;

/// <summary>
/// Marks a service interface as a gated mutating service: every public method must carry a
/// <see cref="RequireGlobalRoleAttribute"/> or <see cref="RequireSiteRoleAttribute"/> (enforced by
/// architecture test A2), and the interface must be registered via
/// <see cref="MutatingServiceRegistration.AddMutatingService{TInterface,TImpl}"/> so it is proxied.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false)]
public sealed class MutatingServiceAttribute : Attribute
{
}

/// <summary>
/// DI wiring for the declarative service-layer gate (design doc 06, gate 9). Mutating services are
/// registered through the <c>AddMutatingService</c> overloads, which wrap the implementation in a
/// Castle DynamicProxy interface proxy backed by <see cref="MethodSecurityInterceptor"/>.
/// </summary>
public static class MutatingServiceRegistration
{
    /// <summary>Registers the proxy generator, the interceptor, and the scoped audit-detail context.</summary>
    public static IServiceCollection AddNetOptGates(this IServiceCollection services)
    {
        services.AddSingleton<ProxyGenerator>();
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<MethodSecurityInterceptor>();
        return services;
    }

    /// <summary>
    /// Registers a mutating service so callers resolving <typeparamref name="TInterface"/> get a gated
    /// proxy of <typeparamref name="TImpl"/>. The implementation is constructed inside the proxy factory
    /// rather than registered as its own service type, so there is no ungated concrete registration for
    /// a component to inject around the gate.
    /// </summary>
    public static IServiceCollection AddMutatingService<TInterface, TImpl>(this IServiceCollection services)
        where TInterface : class
        where TImpl : class, TInterface
        => services.AddMutatingService<TInterface>(sp => ActivatorUtilities.CreateInstance<TImpl>(sp));

    /// <summary>
    /// Registers a gated mutating service whose instance comes from a factory - used for the per-site
    /// registries, which own the instance for the current site's slug.
    /// </summary>
    public static IServiceCollection AddMutatingService<TInterface>(
        this IServiceCollection services, Func<IServiceProvider, TInterface> factory)
        where TInterface : class
    {
        services.AddScoped(sp => Proxy(sp, factory(sp)));
        return services;
    }

    /// <summary>
    /// Registers a gated interface over an existing singleton implementation. The singleton keeps its
    /// lifetime (and its cached state); the gated <typeparamref name="TInterface"/> is scoped because
    /// the interceptor authorizes and audits against the per-request/per-circuit caller.
    /// </summary>
    public static IServiceCollection AddMutatingSingleton<TInterface, TImpl>(this IServiceCollection services)
        where TInterface : class
        where TImpl : class, TInterface
    {
        services.AddSingleton<TImpl>();
        services.AddScoped(sp => Proxy<TInterface>(sp, sp.GetRequiredService<TImpl>()));
        return services;
    }

    private static TInterface Proxy<TInterface>(IServiceProvider sp, TInterface target)
        where TInterface : class
    {
        var generator = sp.GetRequiredService<ProxyGenerator>();
        var interceptor = sp.GetRequiredService<MethodSecurityInterceptor>();
        return generator.CreateInterfaceProxyWithTargetInterface(target, interceptor.ToInterceptor());
    }
}
