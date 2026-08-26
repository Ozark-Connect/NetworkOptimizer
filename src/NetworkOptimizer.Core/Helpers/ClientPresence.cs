namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// What an access point's own agent says about a client being associated right now. Unknown means
/// the agent path cannot vouch either way, and the Console rules apply exactly as they do on a
/// site with no agents.
/// </summary>
public enum AgentClientPresence
{
    /// <summary>The agent path has no answer for this access point.</summary>
    Unknown,

    /// <summary>An agent-covered access point currently holds the client.</summary>
    Present,

    /// <summary>The claimed access point's agent answered with clients, and this is not one.</summary>
    Absent,
}

/// <summary>
/// Whether a wireless client the access point still lists is actually there.
///
/// An access point keeps a client in its association table long after the client has physically
/// left, and the UniFi Console reports that table faithfully - one measured case sat at 50 minutes
/// idle with the signal at the noise floor, still authorized, while the device was twenty miles
/// away. Every surface that asks the Console who is connected inherits that, which is why this
/// lives here rather than in any one of them.
///
/// The measure is idle time: how long since the access point last heard anything from the client.
/// It is the only signal that survives multi-link. "Has never carried traffic" does not - an MLO
/// client associates once per band under its own randomised MAC, and one link carrying a few bytes
/// at association makes the whole client read as alive indefinitely.
/// </summary>
public static class ClientPresence
{
    /// <summary>
    /// How long an access point may have heard nothing before a client stops counting as present.
    ///
    /// Generous on purpose, because the two populations are far apart: a client that is merely
    /// quiet still ARPs and answers probes, so it sits at seconds, while one that has left runs to
    /// tens of minutes or hours. Nothing needs to be tuned finely inside that gap. Erring towards
    /// "still here" costs a stale dot on a map; erring the other way blinks a live client off it.
    /// </summary>
    public const long MaxIdleSeconds = 600;

    /// <summary>
    /// True when the access point has heard from this client recently enough to call it present.
    /// A client reporting no idle time at all is treated as present: absent evidence is not
    /// evidence of absence, and dropping clients on a missing field would empty a map.
    /// </summary>
    /// <param name="idleSeconds">Seconds since the access point last heard from the client.</param>
    public static bool IsPresent(long? idleSeconds)
        => idleSeconds is not { } idle || idle <= MaxIdleSeconds;

    /// <summary>
    /// The full Console-entry gate: the agent's verdict where its access point is covered, the
    /// idle tolerance plus association evidence otherwise. Both Console entry points (topology
    /// discovery, the Wi-Fi Optimizer roster) call this so the surfaces they feed cannot disagree.
    /// </summary>
    /// <param name="idleSeconds">Seconds since the access point last heard from the client.</param>
    /// <param name="apMac">The access point the Console says holds the client.</param>
    /// <param name="radio">The Console's band/radio token for the client.</param>
    /// <param name="signalDbm">The Console's signal reading for the client.</param>
    /// <param name="hasMloLinks">Whether the Console reports any MLO links for the client.</param>
    /// <param name="agent">The agent verdict, Unknown wherever no agent can vouch.</param>
    public static bool IsPresent(
        long? idleSeconds,
        string? apMac,
        string? radio,
        int? signalDbm,
        bool hasMloLinks,
        AgentClientPresence agent) => agent switch
    {
        AgentClientPresence.Present => true,
        AgentClientPresence.Absent => false,
        _ => IsPresent(idleSeconds) && HasAssociationEvidence(apMac, radio, signalDbm, hasMloLinks),
    };

    /// <summary>
    /// Whether a Console-listed wireless client shows any evidence of an association: an access
    /// point, a band, a signal reading, or an MLO link. A departed client can linger in the active
    /// list with every one of these empty and no idle time at all; requiring ALL of them empty is
    /// what keeps a client the Console is mid-refresh on from blinking.
    /// </summary>
    public static bool HasAssociationEvidence(string? apMac, string? radio, int? signalDbm, bool hasMloLinks = false)
        => !string.IsNullOrEmpty(apMac)
        || !string.IsNullOrEmpty(radio)
        || signalDbm is not (null or 0)
        || hasMloLinks;

    /// <summary>
    /// The lowest idle time across a multi-link client's links, or null when it has none. A client
    /// is present if ANY of its links has heard from it, so the minimum is the honest answer.
    /// </summary>
    public static long? LowestIdle(IEnumerable<long> perLinkIdleSeconds)
    {
        long? lowest = null;
        foreach (var idle in perLinkIdleSeconds)
        {
            if (lowest is null || idle < lowest) lowest = idle;
        }
        return lowest;
    }
}
