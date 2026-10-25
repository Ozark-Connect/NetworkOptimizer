namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Names of the firewall policies Automatic Blocking writes to UniFi Network. They live here so the
/// writer (NetworkOptimizer.Threats) and the Security Audit, which does not reference it, share one copy.
/// </summary>
public static class ManagedFirewallPolicies
{
    /// <summary>Blocks the sources Automatic Blocking has blocked itself.</summary>
    public const string Blocklist = "Net. Opt. Blocklist";

    /// <summary>Blocks the public block lists, inbound.</summary>
    public const string PublicLists = "Net. Opt. Public Lists";

    /// <summary>Stops devices reaching the public block lists' addresses.</summary>
    public const string PublicListsOutbound = "Net. Opt. Public Lists (Outbound)";

    /// <summary>Lets an exception's traffic through ahead of the public lists.</summary>
    public const string Exception = "Net. Opt. Exception";

    /// <summary>Whether <paramref name="name"/> is one of the policies above.</summary>
    public static bool IsManaged(string? name) =>
        name is Blocklist or PublicLists or PublicListsOutbound or Exception;
}
