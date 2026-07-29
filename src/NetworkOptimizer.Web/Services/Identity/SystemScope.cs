namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Convenience for entering the explicit system caller scope on a freshly created DI scope
/// (design doc 06). Background work - schedulers, pollers, and the anonymous public endpoints -
/// resolves gated services from its own scope, which has no user caller; without this the gate
/// throws by design rather than silently running unauthorized.
/// </summary>
public static class SystemScope
{
    /// <summary>
    /// Marks <paramref name="scopedProvider"/>'s caller context as the named system actor until the
    /// returned handle is disposed. The actor name lands in the audit log as <c>system:&lt;actor&gt;</c>.
    /// </summary>
    public static IDisposable Enter(IServiceProvider scopedProvider, string systemActor)
        => scopedProvider.GetRequiredService<ICallerContext>().BeginSystemScope(systemActor);
}
