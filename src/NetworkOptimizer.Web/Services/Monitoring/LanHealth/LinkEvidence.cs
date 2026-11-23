namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>Counter growth over the window for one end of a link; null fields mean the end does not report that counter.</summary>
public sealed class EndCounters
{
    public double? RxErrors { get; set; }
    public double? TxErrors { get; set; }
    public double? RxDiscards { get; set; }
    public double? TxDiscards { get; set; }
    public double? RxPackets { get; set; }
    public double? TxPackets { get; set; }
    public double? RxBroadcast { get; set; }
    public double? RxMulticast { get; set; }
    public double? LateCollisions { get; set; }
    public double? StpChanges { get; set; }
    /// <summary>The source of the error counts, for "which end" sentences and coverage.</summary>
    public LinkSource? ErrorSource { get; set; }
    /// <summary>A gateway LAN port's own counters read 0 whatever happens (calibration finding 2): never evidence of a clean link.</summary>
    public bool ErrorsBlind { get; set; }
}

/// <summary>One hour's rates and counters for a link, for baselines and the history detectors.</summary>
public sealed record LinkHourSample(
    DateTime Hour,
    double? MaxInBps,
    double? MaxOutBps,
    double? MeanInBps,
    double? MeanOutBps,
    double? BroadcastPps,
    double? MulticastPps,
    double? PacketsPps,
    double? Discards,
    double? Errors,
    double? TxPackets = null);

/// <summary>A speed or duplex reading at a time (console or kernel).</summary>
public sealed record SpeedReading(DateTime At, int SpeedMbps, bool? FullDuplex, LinkSource Source);

/// <summary>A mesh backhaul reading at a time.</summary>
public sealed record MeshReading(DateTime At, int? SignalDbm, long? TxRateKbps, int? MloMembers, string? ParentMac);

/// <summary>A speed test that crossed the link.</summary>
public sealed record LinkSpeedTest(DateTime At, double DownMbps, double UpMbps, int? Retransmits, string Shape, string Kind, double? NormDownMbps, double? NormUpMbps, bool DuringIncident);

/// <summary>Everything known about one link over the scoring window, from every source.</summary>
public sealed class LinkEvidence
{
    public LinkRecord Link { get; init; } = new();
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public List<LinkEvent> Events { get; init; } = [];
    public List<LinkEventAligner.Observer> Observers { get; init; } = [];
    public EndCounters Upstream { get; init; } = new();
    public EndCounters Downstream { get; init; } = new();
    /// <summary>Hourly samples for the window, newest last.</summary>
    public List<LinkHourSample> Hours { get; init; } = [];
    /// <summary>Hourly samples before the window (up to 4 weeks), for "its own normal".</summary>
    public List<LinkHourSample> BaselineHours { get; init; } = [];
    public List<SpeedReading> Speeds { get; init; } = [];
    /// <summary>The speed this link usually negotiates (the fastest seen over the baseline window).</summary>
    public int? UsualSpeedMbps { get; init; }
    public List<MeshReading> Mesh { get; init; } = [];
    public List<MeshReading> MeshBaseline { get; init; } = [];
    /// <summary>Reboots of the downstream device in the window.</summary>
    public List<DateTime> DownstreamReboots { get; init; } = [];
    /// <summary>PoE draw readings on the upstream port.</summary>
    public List<(DateTime At, double Watts)> PoeDraw { get; init; } = [];
    /// <summary>Probe loss over the window to devices below the link (APs only; switches are poor targets).</summary>
    public double? ProbeLossPct { get; init; }
    /// <summary>Loss to the shared parent's own probe target, so subtree loss can be told from upstream loss.</summary>
    public double? ParentProbeLossPct { get; init; }
    /// <summary>On an access port: the client was idle (sleep, WoL standby) in these spans.</summary>
    public List<(DateTime Start, DateTime End)> ClientIdle { get; init; } = [];
    /// <summary>SFP receive power now and its 7-day best, dBm; module temperature now.</summary>
    public double? SfpRxPowerDbm { get; init; }
    public double? SfpRxPowerBestDbm { get; init; }
    public double? SfpTemperatureC { get; init; }
    public List<LinkSpeedTest> SpeedTests { get; init; } = [];
    /// <summary>Clean days of history before the first unexplained drop, when known.</summary>
    public int? HistoryDays { get; init; }
}
