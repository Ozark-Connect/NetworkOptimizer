namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Defaults for the Add a site wizard. Upstream registers nothing, so <c>IOptions</c> yields these
/// defaults; a hosted deployment overrides them with <c>services.Configure&lt;SiteSetupOptions&gt;</c>.
/// </summary>
public sealed class SiteSetupOptions
{
    /// <summary>
    /// Start every new site on the Connect Through an Agent path: the Agent step runs before the
    /// Console step. For a host that should never be pointed at a console's public address.
    /// </summary>
    public bool ConsoleThroughAgentByDefault { get; set; }
}
