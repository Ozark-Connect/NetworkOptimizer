namespace NetworkOptimizer.Web.Services;

/// <summary>The badge on a Settings card whose setting applies beyond the site in context.</summary>
public sealed record ScopeBadgeText(string Label, string Tooltip)
{
    public static readonly ScopeBadgeText Instance = new("Global", "Applies to all sites (instance-wide)");
}

/// <summary>
/// What the scope badge says to the caller. Here every such setting is the instance's; a host that
/// partitions the instance wraps this to name the caller's own scope instead.
/// </summary>
public interface IScopeBadgeText
{
    Task<ScopeBadgeText> GetAsync();
}

/// <inheritdoc />
public sealed class InstanceScopeBadgeText : IScopeBadgeText
{
    public Task<ScopeBadgeText> GetAsync() => Task.FromResult(ScopeBadgeText.Instance);
}
