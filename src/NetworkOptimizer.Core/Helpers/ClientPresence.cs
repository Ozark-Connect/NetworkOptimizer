namespace NetworkOptimizer.Core.Helpers;

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
