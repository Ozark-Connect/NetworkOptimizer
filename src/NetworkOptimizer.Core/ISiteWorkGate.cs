namespace NetworkOptimizer.Core;

/// <summary>
/// Asks whether background work may run for a site at all. The ONE gate every per-site loop
/// consults, so that the reasons a site can be closed - licensing today, subscription lapse and
/// operator suspension next - are answered in one place rather than re-implemented per poller.
///
/// Deliberately narrow and synchronous: implementations answer from a cached snapshot, so a loop can
/// call this per site per cycle without a database round trip. Lives in Core because the background
/// loops that need it are spread across Alerts, Threats, and Web, and only Core is visible to all of
/// them; the real implementation is the licensing state service in Web.
///
/// Fail-open: an implementation that does not yet know the answer returns true. A licensing problem
/// must never take monitoring down harder than the policy actually demands.
/// </summary>
public interface ISiteWorkGate
{
    /// <summary>
    /// True when background work may run for the given site slug. Unknown slugs read as operational.
    ///
    /// Null or empty means the default site. The background loops in Alerts and Threats identify the
    /// default site by an empty site key while the licensing snapshot keys it by its real slug, and
    /// an unnormalized empty string would look like an unknown slug and read as operational forever -
    /// a gate that silently never closes on the commonest install. Implementations normalize.
    /// </summary>
    bool IsSiteOperational(string? slug);
}
