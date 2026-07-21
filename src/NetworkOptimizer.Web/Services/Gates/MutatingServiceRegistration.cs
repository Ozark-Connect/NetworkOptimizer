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
/// registered through <see cref="AddMutatingService{TInterface,TImpl}"/>, which wraps the concrete
/// implementation in a Castle DynamicProxy interface proxy backed by <see cref="MethodSecurityInterceptor"/>.
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
    /// proxy of <typeparamref name="TImpl"/>. Both share the scope; the concrete type stays available
    /// for the proxy's target.
    /// </summary>
    public static IServiceCollection AddMutatingService<TInterface, TImpl>(this IServiceCollection services)
        where TInterface : class
        where TImpl : class, TInterface
    {
        services.AddScoped<TImpl>();
        services.AddScoped(sp =>
        {
            var target = sp.GetRequiredService<TImpl>();
            var generator = sp.GetRequiredService<ProxyGenerator>();
            var interceptor = sp.GetRequiredService<MethodSecurityInterceptor>();
            return generator.CreateInterfaceProxyWithTargetInterface<TInterface>(target, interceptor.ToInterceptor());
        });
        return services;
    }
}
