using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Correlates one SNMP interface (identified by ifIndex and/or Linux ifname, plus its
/// raw SNMP-reported speed) with a device's UniFi port_table entry, yielding the
/// friendly name, port number, negotiated link speed, and SFP flag the port table
/// carries but SNMP gets wrong or missing on gateways.
///
/// This mirrors the per-interface reconcile the directly-monitored slow tier runs
/// (MonitoringCollectionAgent.SlowTierCollectAsync): the SNMP walk supplies the
/// interface enumeration and raw counters/speed, while the UniFi port table fills the
/// friendly name and the correct negotiated speed. It is factored out here so the
/// agent-relayed path (where the server can't SNMP-walk a remote network but the
/// on-site agent already streams the interface list) can apply the identical logic.
///
/// Match strategy (same "two strategies" the slow tier documents):
///   1. ifIndex == port_idx - verified on UniFi switches, where SNMP ifIndex equals
///      the port index. Skipped when ifIndex is unknown (0), e.g. an agent that
///      streams only the Linux ifname.
///   2. port_table.ifname == the interface's Linux ifname - the stable join UniFi
///      exposes for gateways (whose ifIndex != port_idx). The raw SNMP ifname is
///      tried first, then the monitored name, since a gateway physical port with no
///      SNMP alias reports the same ethN name in both.
///
/// Link speed uses "lower of the two wins": SNMP is fresh and correct on switches, but
/// a gateway inflates copper ports to their max capability (ifHighSpeed/ifSpeed both
/// report the ceiling, not the negotiated rate). Negotiated speed can never exceed the
/// SNMP-reported value, so when the port table reports a lower speed we take it. WAN
/// ports have port_table speed 0, so SNMP wins there.
/// </summary>
public static class InterfacePortCorrelation
{
    /// <summary>The port-table-derived facts for a single interface.</summary>
    public readonly record struct Result(
        string? FriendlyName,
        int? PortNumber,
        int? LinkSpeedMbps,
        bool? IsSfp);

    /// <summary>
    /// Correlates an interface with the device's port table.
    /// </summary>
    /// <param name="portTable">The device's UniFi port_table (may be null).</param>
    /// <param name="ifIndex">SNMP ifIndex, or 0/negative when unknown.</param>
    /// <param name="snmpSpeedBps">
    /// The SNMP-reported speed in bits per second (ifHighSpeed*1e6 or ifSpeed), or 0
    /// when unknown.
    /// </param>
    /// <param name="rawIfName">The raw SNMP ifName (Linux name, e.g. "eth4").</param>
    /// <param name="monitoredIfName">
    /// The interface's monitored name (alias-or-ifName), tried as a secondary ifname
    /// join when the raw ifName didn't match.
    /// </param>
    public static Result Correlate(
        IReadOnlyList<SwitchPort>? portTable,
        int ifIndex,
        long snmpSpeedBps,
        string? rawIfName,
        string? monitoredIfName)
    {
        var portMatch = MatchPort(portTable, ifIndex, rawIfName, monitoredIfName);

        int? portNumber = portMatch is { PortIdx: > 0 } ? portMatch.PortIdx : null;
        var friendlyName = string.IsNullOrEmpty(portMatch?.Name) ? null : portMatch!.Name;
        var isSfp = portMatch?.SfpFound;

        int? snmpSpeedMbps = snmpSpeedBps > 0 ? (int)(snmpSpeedBps / 1_000_000) : (int?)null;
        int? unifiSpeedMbps = portMatch is { Speed: > 0 } ? portMatch.Speed : (int?)null;
        int? linkSpeedMbps = (snmpSpeedMbps, unifiSpeedMbps) switch
        {
            (null, null) => null,
            (null, var u) => u,
            (var s, null) => s,
            var (s, u) => Math.Min(s.Value, u.Value),
        };

        return new Result(friendlyName, portNumber, linkSpeedMbps, isSfp);
    }

    private static SwitchPort? MatchPort(
        IReadOnlyList<SwitchPort>? portTable, int ifIndex, string? rawIfName, string? monitoredIfName)
    {
        if (portTable == null) return null;

        SwitchPort? match = null;
        if (ifIndex > 0)
            match = portTable.FirstOrDefault(p => p.PortIdx == ifIndex);
        match ??= MatchByIfName(portTable, rawIfName);
        match ??= MatchByIfName(portTable, monitoredIfName);
        return match;
    }

    private static SwitchPort? MatchByIfName(IReadOnlyList<SwitchPort> portTable, string? ifName)
    {
        if (string.IsNullOrEmpty(ifName)) return null;
        return portTable.FirstOrDefault(p =>
            !string.IsNullOrEmpty(p.IfName)
            && string.Equals(p.IfName, ifName, StringComparison.OrdinalIgnoreCase)
            && p.PortIdx > 0);
    }
}
