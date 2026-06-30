namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Well-known ASNs that appear on traceroutes but are not commercial transit, so ISP
/// Health must not grade, chart, or display them as Transit. They surface on a path
/// because of IXP route servers or anycast DNS endpoints, not because they haul our
/// traffic upstream. Discovery never proposes them as transit targets, and the scoring,
/// chart, and live-stats read paths drop any rows already committed.
/// </summary>
internal static class WellKnownAsns
{
    /// <summary>
    /// WoodyNet / Packet Clearing House (PCH): operates IXP route servers and anycast
    /// DNS infrastructure, not a transit carrier (AS42 = WOODYNET-1, AS715 = WOODYNET-2).
    /// Exposed as an array so EF Core translates Contains() to a SQL IN on the DB read paths.
    /// </summary>
    public static readonly int[] NonTransitInfrastructure = { 42, 715 };
}
