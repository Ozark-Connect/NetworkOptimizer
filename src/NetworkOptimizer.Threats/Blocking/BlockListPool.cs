namespace NetworkOptimizer.Threats.Blocking;

/// <summary>
/// Which sites share the downloaded copy of a public list that needs a key. Any site in the pool
/// with its own key keeps the copy fresh, and every site in the pool can enforce it without one.
/// Keys are never shared: each stays with the site that entered it. The public build pools the
/// whole instance; a multi-tenant host pools each tenant by wrapping this interface (one customer's
/// subscription must not serve another). Lists without a key are one copy per instance either way.
/// </summary>
public interface IBlockListPool
{
    /// <summary>The pool <paramref name="site"/> (null is the default site) belongs to. Empty is the instance.</summary>
    Task<string> GetPoolAsync(string? site, CancellationToken cancellationToken);
}

/// <summary>One pool: every site on the instance.</summary>
public sealed class InstanceBlockListPool : IBlockListPool
{
    public Task<string> GetPoolAsync(string? site, CancellationToken cancellationToken) => Task.FromResult("");
}
