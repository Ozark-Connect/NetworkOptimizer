namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// How "publicly routable" an IPv4/IPv6 WAN address actually is. Used by the upstream
/// tracer wizard (spec 5.5) to honestly surface degraded cases instead of producing
/// garbage downstream. The values here aren't persisted, so renumbering is safe.
/// </summary>
public enum PublicAddressClass
{
    /// <summary>Couldn't classify - missing or unparseable address.</summary>
    Unknown = 0,

    /// <summary>Normal public IPv4. Tracer can proceed.</summary>
    PublicIPv4 = 1,

    /// <summary>
    /// 100.64.0.0/10 - the gateway sees a CGNAT-range WAN IP. Tracer can still find
    /// the first real hop and resolve the access ISP from there; access cloud renders
    /// with a "(CGNAT)" qualifier.
    /// </summary>
    Cgnat = 2,

    /// <summary>
    /// RFC1918 on the WAN side - double-NAT. Out of MVP tracer scope per spec 7; we
    /// detect and surface it explicitly rather than silently failing.
    /// </summary>
    DoubleNat = 3,

    /// <summary>
    /// IPv6 WAN address (no IPv4). Out of MVP tracer scope per spec 7. Surface
    /// explicitly so the user understands why discovery isn't running.
    /// </summary>
    IPv6 = 4,

    /// <summary>
    /// Loopback/link-local on the WAN - the gateway is misconfigured. Surface the
    /// issue rather than attempting discovery.
    /// </summary>
    Misconfigured = 5,

    /// <summary>
    /// 0.0.0.0/8 or 224.0.0.0/4: "public-looking" but reserved/multicast space, not
    /// globally routed. We can't trace from these.
    /// </summary>
    NonGloballyRouted = 6
}
