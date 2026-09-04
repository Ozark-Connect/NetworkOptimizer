using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Core.Interfaces;

/// <summary>
/// Answers whether an access point's own agent currently holds a client, for the Console entry
/// points that otherwise judge presence from idle time alone. Unknown whenever the agent path
/// cannot vouch either way - access point not covered, claim expired, or an answer that named no
/// clients - and the caller then applies the Console rules exactly as on a site with no agents.
/// </summary>
public interface IAgentClientPresenceSource
{
    /// <summary>
    /// The agent verdict for one client. <paramref name="apMac"/> is where the Console says the
    /// client is; a client any covered access point holds is Present even when that is a different
    /// access point, because a roam the agent has seen is not a departure.
    /// </summary>
    AgentClientPresence PresenceFor(string? apMac, string? clientMac);
}
