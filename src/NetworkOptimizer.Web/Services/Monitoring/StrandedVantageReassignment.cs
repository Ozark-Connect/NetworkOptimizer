using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Moves WAN vantages whose agent is gone onto the site's gateway agent, bound to each WAN's own
/// data-path interface - the same binding the vantage form fills for a gateway agent.
/// <para>
/// A vantage is stranded when its agent was removed, or has been offline longer than
/// <see cref="OfflineGrace"/>. Its targets stay with it, so nothing on the WAN is probed until
/// someone notices; the gateway agent is the one box on the site that can reach every WAN by
/// itself. When an offline agent reconnects, its vantages go back to it with their old binding:
/// left on the gateway, an agent that is policy-routed out one WAN would hold no vantage, read as
/// unsteered, and collect the primary's targets over the wrong WAN. A removed agent's move is
/// permanent.
/// </para>
/// </summary>
public static class StrandedVantageReassignment
{
    /// <summary>
    /// How long an agent may stay offline before its vantages move. Long enough to ride out an
    /// agent upgrade or a gateway reboot, which must not rewrite anyone's configuration.
    /// </summary>
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromMinutes(15);

    /// <summary>What the registry knows about one of the site's enrolled agents.</summary>
    /// <param name="AgentId">Registry id.</param>
    /// <param name="LastSeenAt">Last heartbeat, null when never seen.</param>
    public sealed record EnrolledAgent(int AgentId, DateTime? LastSeenAt);

    /// <summary>
    /// The vantages to move to <paramref name="gatewayAgentId"/>: each one assigned to an agent
    /// that is not connected and is either no longer enrolled on the site or offline for
    /// <see cref="OfflineGrace"/>. A vantage with no agent is never stranded: the server binds it,
    /// and no agent outage touches it.
    /// </summary>
    /// <param name="watchingSince">
    /// When this server started. Offline time counts from here at the earliest: an agent cannot
    /// heartbeat to a server that is down, so its last sighting says nothing about that stretch.
    /// </param>
    public static List<WanContext> SelectStranded(
        IEnumerable<WanContext> contexts,
        IReadOnlyCollection<EnrolledAgent> enrolledAgents,
        IReadOnlyCollection<int> connectedAgentIds,
        int gatewayAgentId,
        DateTime now,
        DateTime watchingSince)
    {
        var byId = enrolledAgents.ToDictionary(a => a.AgentId);
        return contexts
            .Where(c => c.AgentId is int id
                && id != gatewayAgentId
                && !connectedAgentIds.Contains(id)
                && (!byId.TryGetValue(id, out var agent)
                    || now - Later(agent.LastSeenAt, watchingSince) >= OfflineGrace))
            .ToList();
    }

    private static DateTime Later(DateTime? seen, DateTime watchingSince) =>
        seen is DateTime s && s > watchingSince ? s : watchingSince;

    /// <summary>
    /// Moves a stranded context onto the gateway agent, bound to <paramref name="dataPath"/>.
    /// An offline agent's binding is kept so <see cref="Restore"/> can hand it back; the first
    /// owner is the one remembered, so a vantage that moves twice still returns to where it began.
    /// A removed agent is not remembered: it cannot come back.
    /// </summary>
    public static void MoveToGateway(WanContext context, int gatewayAgentId, string dataPath, bool agentRemoved)
    {
        if (context.ReassignedFromAgentId == null && !agentRemoved)
        {
            context.ReassignedFromAgentId = context.AgentId;
            context.ReassignedFromInterfaceName = context.InterfaceName;
            context.ReassignedFromProbeSourceIp = context.ProbeSourceIp;
        }
        context.AgentId = gatewayAgentId;
        context.InterfaceName = dataPath;
        context.ProbeSourceIp = null;
    }

    /// <summary>
    /// Hands a moved context back to its original agent with its original binding. Returns the
    /// agent that held it, which has to be told it no longer probes these targets.
    /// </summary>
    public static int? Restore(WanContext context)
    {
        var holder = context.AgentId;
        context.AgentId = context.ReassignedFromAgentId;
        context.InterfaceName = context.ReassignedFromInterfaceName;
        context.ProbeSourceIp = context.ReassignedFromProbeSourceIp;
        Forget(context);
        return holder;
    }

    /// <summary>
    /// Drops the return record of every context whose original agent is no longer enrolled, making
    /// its move permanent. Returns whether anything changed.
    /// </summary>
    public static bool ForgetRemovedOrigins(IEnumerable<WanContext> contexts, IReadOnlyCollection<EnrolledAgent> enrolledAgents)
    {
        var changed = false;
        foreach (var context in contexts)
        {
            if (context.ReassignedFromAgentId is not int origin
                || enrolledAgents.Any(a => a.AgentId == origin))
                continue;
            Forget(context);
            changed = true;
        }
        return changed;
    }

    /// <summary>Clears the return record, leaving the context where it is now.</summary>
    public static void Forget(WanContext context)
    {
        context.ReassignedFromAgentId = null;
        context.ReassignedFromInterfaceName = null;
        context.ReassignedFromProbeSourceIp = null;
    }

    /// <summary>
    /// The interface a gateway agent binds to reach this WAN: its persisted data path, so eth4,
    /// eth4.100, ppp0 or gre1 rather than the physical port. Null when no profile records one,
    /// and a vantage with no known data path is left where it is - bound to nothing, the gateway
    /// agent would probe its default route and file the result under this WAN.
    /// </summary>
    public static string? DataPathFor(IEnumerable<WanProfile> profiles, string? wanInterface)
    {
        if (string.IsNullOrEmpty(wanInterface)) return null;
        var key = GatewayWanHelper.WanInterfaceKeyFromKey(wanInterface);
        var dataPath = profiles.FirstOrDefault(p =>
            !string.IsNullOrEmpty(p.WanNetworkgroup)
            && string.Equals(GatewayWanHelper.WanInterfaceKeyFromKey(p.WanNetworkgroup), key,
                StringComparison.OrdinalIgnoreCase))?.DataPathInterface;
        return string.IsNullOrEmpty(dataPath) ? null : dataPath;
    }
}
