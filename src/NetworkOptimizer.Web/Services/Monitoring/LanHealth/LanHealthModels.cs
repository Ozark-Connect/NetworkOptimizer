using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// What a link joins. A fabric link joins two UniFi devices (a wired uplink or a mesh backhaul); an
/// access port faces an end device. Not the STP Edge / Infrastructure port mode: that is a setting,
/// this is topology, and the STP checks flag where the two disagree.
/// </summary>
public enum LinkKind
{
    FabricWired,
    FabricMesh,
    FabricMeshMlo,
    Access,
}

/// <summary>Which end of a link an observation is about.</summary>
public enum LinkSide
{
    Upstream,
    Downstream,
}

/// <summary>Where an observation came from. Coverage is counted per source.</summary>
public enum LinkSource
{
    /// <summary>The AP Agent's kernel PHY line, exact to the second.</summary>
    ApKernelLog,
    /// <summary>The AP Agent's carrier_changes counter.</summary>
    ApCarrierCounter,
    /// <summary>The AP Agent's receive CRC counter (ethtool -S).</summary>
    ApCrcCounter,
    /// <summary>The On-Site Agent reading a gateway's switch chip.</summary>
    GatewaySwitchChip,
    /// <summary>The switch's own link_down_count in the UniFi port table.</summary>
    SwitchLinkDownCount,
    /// <summary>SNMP ifLastChange.</summary>
    SnmpLastChange,
    /// <summary>SNMP ifOperStatus.</summary>
    SnmpOperStatus,
    /// <summary>SNMP error and discard counters.</summary>
    SnmpCounters,
    /// <summary>The UniFi port table's error, drop, and packet counters.</summary>
    PortTableCounters,
    /// <summary>The UniFi port table's speed, duplex, and connected_at.</summary>
    PortTableState,
    /// <summary>Fabric probes (ICMP every 5 s) to a device below the link.</summary>
    FabricProbe,
    /// <summary>UniFi Port Insights.</summary>
    PortInsights,
    /// <summary>The gateway's kernel log.</summary>
    GatewayKernelLog,
    /// <summary>Context: a Change Control change, a reboot, a device state, a Firmware Rollout wave.</summary>
    Context,
}

/// <summary>What one observation says happened.</summary>
public enum LinkEventType
{
    LinkDown,
    LinkUp,
    /// <summary>A counter (link_down_count, carrier_changes, ifLastChange) says the link went down and up somewhere in the window.</summary>
    Relink,
    SpeedChange,
    DuplexChange,
    ErrorBurst,
    DiscardBurst,
    LossBurst,
    CounterReset,
}

/// <summary>
/// One observation from one source, with a window sized to its precision: a kernel line +-1 s, a
/// probe burst its span, a counter delta the interval between its two samples.
/// </summary>
public sealed record LinkEvent(
    DateTime Start,
    DateTime End,
    LinkEventType Type,
    LinkSource Source,
    LinkSide Side,
    double Count = 1,
    int? SpeedMbps = null,
    string? Note = null)
{
    /// <summary>True for a source that pins the time to a few seconds.</summary>
    public bool Precise => (End - Start) <= TimeSpan.FromSeconds(10);

    /// <summary>The best single time for the event.</summary>
    public DateTime At => Start + (End - Start) / 2;
}

/// <summary>Why an incident is not a fault, as the timeline says it (lanh-38).</summary>
public enum ExplainReason
{
    PortProfileChanged,
    ManualLinkReset,
    DeviceUpgrading,
    DeviceRebooted,
    FirmwareRolloutWave,
    DeviceAsleep,
    /// <summary>The device was provisioning or adopting (a state window).</summary>
    DeviceProvisioning,
}

/// <summary>A span that explains link events on the devices it covers.</summary>
public sealed record ExplainWindow(DateTime Start, DateTime End, string DeviceMac, ExplainReason Reason, string? Detail = null)
{
    public bool Covers(DateTime start, DateTime end) => start <= End && end >= Start;
}

/// <summary>What kind of occurrence an incident is.</summary>
public enum IncidentKind
{
    /// <summary>The link went down and came back within 60 s.</summary>
    Drop,
    /// <summary>A counter says the link relinked; the time is only known to the poll window.</summary>
    Relink,
    /// <summary>The link went down and did not come back within 60 s (the device-offline path owns it).</summary>
    Outage,
    ErrorBurst,
    DiscardBurst,
    /// <summary>Probe loss with no link evidence on this link.</summary>
    LossBurst,
    SpeedChange,
}

/// <summary>Events on one link merged into one occurrence, with every source that saw it and those that should have.</summary>
public sealed class LinkIncident
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public IncidentKind Kind { get; set; }
    public List<LinkEvent> Events { get; } = [];
    public HashSet<LinkSource> SeenBy { get; } = [];
    public HashSet<LinkSource> MissedBy { get; } = [];
    /// <summary>Why this is not a fault; null when it is one.</summary>
    public ExplainReason? Explained { get; set; }
    public string? ExplainedDetail { get; set; }
    /// <summary>Errors that arrived within 60 s after a drop, attached to it rather than scored again.</summary>
    public double AttachedErrors { get; set; }
    /// <summary>How many occurrences this incident stands for (a counter delta of 3 with no precise source is one incident of 3).</summary>
    public double Count { get; set; } = 1;
    public int? SpeedAfterMbps { get; set; }

    public bool IsExplained => Explained != null;

    /// <summary>Seconds the link was down, for a drop with both edges; null otherwise.</summary>
    public double? DownSeconds
    {
        get
        {
            var down = Events.Where(e => e.Type == LinkEventType.LinkDown && e.Precise).Select(e => e.At).DefaultIfEmpty().Min();
            var up = Events.Where(e => e.Type == LinkEventType.LinkUp && e.Precise).Select(e => e.At).DefaultIfEmpty().Max();
            return down != default && up != default && up > down ? (up - down).TotalSeconds : null;
        }
    }

    /// <summary>The most precise time for the incident.</summary>
    public DateTime At => Events.Where(e => e.Precise).OrderBy(e => e.End - e.Start).Select(e => (DateTime?)e.At).FirstOrDefault()
        ?? Start + (End - Start) / 2;
}

/// <summary>One end of a link.</summary>
public sealed class LinkEnd
{
    public string DeviceMac { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public DeviceType DeviceType { get; init; }
    public string Model { get; init; } = "";
    /// <summary>The port on this device, or null for a mesh radio or an end device.</summary>
    public int? PortIdx { get; init; }
    public string? IfName { get; init; }
    public string? PortName { get; init; }
    /// <summary>"Port 3": by number, since a port renamed after its device would repeat the device's name.</summary>
    public string Label => PortIdx is int p ? $"Port {p}" : DeviceName;
    /// <summary>False for an end device: it reports nothing.</summary>
    public bool IsUniFi { get; init; } = true;
    /// <summary>The device reports a jumbo frame setting (jumboframe_enabled); APs do not today.</summary>
    public bool HasJumboSetting { get; init; }
    public bool IsGateway => DeviceType == DeviceType.Gateway;
    public bool IsSwitch => DeviceType == DeviceType.Switch;
    public bool IsAccessPoint => DeviceType == DeviceType.AccessPoint;
}

/// <summary>One member of an MLO mesh backhaul.</summary>
public sealed record MloMember(string Radio, int? Channel, int? SignalDbm, long TxRateKbps, long RxRateKbps);

/// <summary>A port's configuration, read for the config checks and shown on drop findings.</summary>
public sealed class PortConfig
{
    public bool? FlowControlRx { get; init; }
    public bool? FlowControlTx { get; init; }
    public bool? Jumbo { get; init; }
    public bool? StormControlBroadcast { get; init; }
    public bool? StormControlMulticast { get; init; }
    public bool Autoneg { get; init; } = true;
    public string? PortProfileId { get; init; }
    public string? NativeNetworkId { get; init; }
    public string? Forward { get; init; }
    public List<string> ExcludedNetworkIds { get; init; } = [];
    public string? StpState { get; init; }
    public bool? StpEnabled { get; init; }
    public int? MacTableCount { get; init; }
    public string? PoeMode { get; init; }
    public double? PoePowerW { get; init; }
    public string? PoeClass { get; init; }
    public long? LinkDownCount { get; init; }
    public int? Anomalies { get; init; }
    public string? Media { get; init; }
    public bool IsLagMember { get; init; }
    public int? LagIdx { get; init; }
}

/// <summary>
/// One link: identity, kind, both ends, the resolved speed, and the clients it carries. Identity is
/// (upstream mac, upstream port) - (downstream mac, downstream port), or the upstream port alone for
/// an access port.
/// </summary>
public sealed class LinkRecord
{
    public string Id { get; init; } = "";
    public LinkKind Kind { get; init; }
    public LinkEnd Upstream { get; init; } = new();
    public LinkEnd Downstream { get; init; } = new();
    /// <summary>Negotiated speed through the shared resolver; null when unknown.</summary>
    public int? SpeedMbps { get; init; }
    public bool? FullDuplex { get; init; }
    /// <summary>Speed set manually on the upstream port.</summary>
    public bool Forced { get; init; }
    /// <summary>The fastest speed both ends support (UniFi speed_caps only), when both report caps.</summary>
    public int? CapabilityMbps { get; init; }
    /// <summary>Clients behind the link (the whole uplink subtree for fabric, the one client for access).</summary>
    public int ClientsBehind { get; init; }
    /// <summary>UniFi devices behind the link, itself included.</summary>
    public int DevicesBehind { get; init; }
    public PortConfig UpstreamPort { get; init; } = new();
    public PortConfig? DownstreamPort { get; init; }
    public List<MloMember> Mlo { get; init; } = [];
    public int? MeshSignalDbm { get; init; }
    public long? MeshTxRateKbps { get; init; }
    public int? UplinkDepth { get; init; }
    /// <summary>The end-device client MAC on an access port, when known.</summary>
    public string? ClientMac { get; init; }

    public bool IsFabric => Kind != LinkKind.Access;
    public bool IsMesh => Kind is LinkKind.FabricMesh or LinkKind.FabricMeshMlo;

    /// <summary>"Gateway A Port 3 to AP 1": the upstream device and port number, then the device on the far side.</summary>
    public string Name => IsMesh || Upstream.PortIdx == null
        ? $"{Upstream.DeviceName} to {Downstream.DeviceName}"
        : $"{Upstream.DeviceName} {Upstream.Label} to {Downstream.DeviceName}";

    /// <summary>The end-device side or downstream UniFi device's MAC, used to tie probes and agent events to the link.</summary>
    public string DownstreamMac => Downstream.DeviceMac;
}

/// <summary>A finding's weight on the page (the ISP Health severity pills).</summary>
public enum FindingSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>Detector ids, numbered as the spec numbers them.</summary>
public static class Detectors
{
    public const int LinkDrops = 1, SpeedDownshift = 2, DuplexMismatch = 3, StableLinkErrors = 4, ErrorAsymmetry = 5,
        LossWithoutEvidence = 6, Discards = 7, PowerEvents = 8, L2Instability = 9, LacpMemberDrop = 10, Optics = 11,
        MeshUplink = 12, UnusualUtilization = 13, BelowCapability = 14, BottleneckUplink = 15, AccessDegraded = 16,
        HiddenSwitch = 17, MacMoves = 18, StpChurn = 19, LacpHealth = 20, NativeVlanMismatch = 21, MtuMismatch = 22,
        MulticastHygiene = 23, HairpinLoad = 24, Microbursts = 25, PoeHealth = 26, ManagementPlane = 27, MeshDepth = 28,
        FabricDeviceHealth = 29, DuplicateIp = 30, CapacityTrend = 31, GatewayForwarding = 32, StpConfig = 40;

    /// <summary>Detectors whose findings cost points: faults on the link. Everything else is load, config, or capacity, at 0 pts.</summary>
    public static bool Scores(int detector) => detector is LinkDrops or SpeedDownshift or DuplexMismatch or StableLinkErrors
        or ErrorAsymmetry or LossWithoutEvidence or L2Instability or LacpMemberDrop or Optics or MeshUplink or BelowCapability
        or AccessDegraded or PoeHealth or GatewayForwarding;
}

/// <summary>
/// One finding on a link, in the ISP Health findings pattern: severity, "Class: Port to device"
/// title, what happened, signature chips, and the points it cost.
/// </summary>
public sealed class LinkFinding
{
    public int Detector { get; init; }
    public string LinkId { get; init; } = "";
    public FindingSeverity Severity { get; init; }
    /// <summary>The finding class ("Link Flapping"); the title is "{Class}: {link name}".</summary>
    public string Class { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public List<string> Chips { get; init; } = [];
    /// <summary>Points off the link score; 0 for load, config and capacity findings.</summary>
    public double Points { get; set; }
    public DateTime? Onset { get; init; }
    /// <summary>The status pill this finding sets on the link (lanh-21), when it sets one.</summary>
    public LinkStatus? Status { get; init; }
    /// <summary>Next steps to show on the link page.</summary>
    public List<string> NextSteps { get; init; } = [];
}

/// <summary>The Links table's status pill (lanh-21).</summary>
public enum LinkStatus
{
    Healthy,
    Flapping,
    Errors,
    SpeedDegraded,
    Storm,
    Busy,
    Muted,
    Excluded,
}

/// <summary>How an exclusion treats a link (lanh-44).</summary>
public enum ExclusionLevel
{
    /// <summary>Keep scoring and history; no alerts.</summary>
    MuteAlerts = 0,
    /// <summary>No score and no alerts; still shown greyed with its data.</summary>
    ExcludeFromScore = 1,
}

/// <summary>Per source, for one link: how many incidents it caught of those it should have.</summary>
public sealed record SourceCoverage(LinkSource Source, int Caught, int Total, bool Observing, DateTime? Since);

/// <summary>One link's evaluation over a window.</summary>
public sealed class LinkHealth
{
    public LinkRecord Link { get; init; } = new();
    public double Score { get; set; } = 100;
    public List<LinkIncident> Incidents { get; init; } = [];
    public List<LinkFinding> Findings { get; init; } = [];
    public List<SourceCoverage> Coverage { get; init; } = [];
    public int Drops => (int)Incidents.Where(i => !i.IsExplained && i.Kind is IncidentKind.Drop or IncidentKind.Relink).Sum(i => i.Count);
    public int DropsExplained => (int)Incidents.Where(i => i.IsExplained && i.Kind is IncidentKind.Drop or IncidentKind.Relink).Sum(i => i.Count);
    public double ErrorsUpstreamRx { get; set; }
    public double ErrorsDownstreamRx { get; set; }
    public double DiscardsUpstreamTx { get; set; }
    public double DiscardsDownstreamTx { get; set; }
    public double? ProbeLossPct { get; set; }
    public ExclusionLevel? Exclusion { get; set; }
    public LinkStatus Status { get; set; } = LinkStatus.Healthy;
    public DateTime? Onset { get; set; }
    /// <summary>Days of clean history before the onset, when the history reaches that far.</summary>
    public int? CleanDaysBefore { get; set; }

    // The latest since-boot counters each end keeps (null: it keeps none), for the Both Ends card.
    public double? UpLinkDownCount { get; set; }
    public double? DownLinkDownCount { get; set; }
    public double? DownCarrierChanges { get; set; }
    public double? PoeWatts { get; set; }
}

/// <summary>The site's LAN Health over a window: the score card, the findings, and every link.</summary>
public sealed class LanHealthReport
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public double? Score { get; set; }
    public int FabricLinks { get; set; }
    public int AccessPorts { get; set; }
    public int Incidents { get; set; }
    public int Explained { get; set; }
    public List<LinkHealth> Links { get; init; } = [];
    public List<LinkFinding> Findings { get; init; } = [];
    /// <summary>Hourly site scores for the Score History card.</summary>
    public List<(DateTime Hour, double Score)> History { get; init; } = [];
    /// <summary>The first hour LAN Health replaced Site Health's LAN inputs, for the cutover mark.</summary>
    public DateTime? CutoverAt { get; set; }
    /// <summary>Interface classes left out of scoring, for the Links footer (lanh-22).</summary>
    public List<string> NotScored { get; init; } = [];
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
    /// <summary>False until the first roll-up has run for the site.</summary>
    public bool Ready { get; set; }
}
