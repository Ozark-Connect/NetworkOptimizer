namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Catalog of hardcoded well-known ASN sets used by upstream classification and ISP
/// Health: the tier-1 carrier set (for core-peering detection) and the non-transit
/// infrastructure set (ASNs that ride a traceroute but never haul our traffic upstream).
/// </summary>
internal static class WellKnownAsns
{
    /// <summary>
    /// WoodyNet / Packet Clearing House (PCH): operates IXP route servers and anycast
    /// DNS infrastructure, not a transit carrier (AS42 = WOODYNET-1, AS715 = WOODYNET-2).
    /// These appear on a path via IX fabric or anycast DNS endpoints, so ISP Health must
    /// not grade, chart, or display them as Transit. Discovery never proposes them as
    /// transit targets, and the scoring, chart, and live-stats read paths drop any rows
    /// already committed. Exposed as an array so EF Core translates Contains() to a SQL IN
    /// on the DB read paths.
    /// </summary>
    public static readonly int[] NonTransitInfrastructure = { 42, 715 };

    /// <summary>
    /// Tier-1 (settlement-free) networks, with the sibling ASNs that show up in real
    /// US traces. Two tier-1s adjacent on a path is core peering, not our access ISP's
    /// transit, so a tier-1 sitting directly above another tier-1 is excluded as a
    /// candidate. Stable set - revisit only on major carrier M&amp;A. Last reviewed 2026-06.
    ///
    /// Hurricane Electric (AS6939) is deliberately NOT included: it isn't settlement-free
    /// on IPv4 (buys paid transit; Cogent won't peer), so its presence beneath a tier-1
    /// carries no reliable "core peering" signal. A tier-1 reached via HE still surfaces
    /// as a candidate and can simply be unchecked in the discovery review list.
    /// </summary>
    public static readonly HashSet<int> Tier1 = new()
    {
        3356, 209, 3549, 3561,            // Lumen / Level 3 / CenturyLink / Global Crossing / Savvis
        7018, 2386, 7132, 6389, 2686,     // AT&T (incl. legacy SBC / BellSouth ASNs seen in US traces)
        701, 702, 703,     // Verizon (UUNET)
        2914,              // NTT (GIN)
        174, 1239,         // Cogent (1239 = ex-SprintLink, sold to Cogent 2023)
        1299,              // Arelion (ex-Telia)
        3257,              // GTT (backbone now EXA Infrastructure; ASN still registered GTT)
        6453,              // Tata
        6461,              // Zayo
        6762,              // Telecom Italia Sparkle
        3491,              // PCCW Global
        5511,              // Orange
        12956,             // Telxius (Telefonica)
        3320,              // Deutsche Telekom
    };
}
