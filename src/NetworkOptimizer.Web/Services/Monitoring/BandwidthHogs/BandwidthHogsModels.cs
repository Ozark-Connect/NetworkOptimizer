namespace NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;

/// <summary>One client in the Bandwidth Hogs list. Rates are the instant's, bytes the window's;
/// a row from one mode leaves the other's figures at zero.</summary>
public sealed record HogRow
{
    public required string ClientMac { get; init; }
    public string? Name { get; init; }
    public string? Ip { get; init; }
    public bool IsWired { get; init; }

    /// <summary>"2.4" / "5" / "6" for a wireless client.</summary>
    public string? Band { get; init; }

    /// <summary>The access point or switch the client hangs off.</summary>
    public string? ViaDevice { get; init; }

    /// <summary>The switch port label for a wired client ("Port 7").</summary>
    public string? ViaPort { get; init; }

    /// <summary>Everything through the client's radio or port, in the client's frame.</summary>
    public double DownBps { get; init; }
    public double UpBps { get; init; }

    /// <summary>The part of that which left through the WAN, per <see cref="WanShareReconciler"/>.</summary>
    public double WanDownBps { get; init; }
    public double WanUpBps { get; init; }

    public long DownBytes { get; init; }
    public long UpBytes { get; init; }
    public long WanDownBytes { get; init; }
    public long WanUpBytes { get; init; }

    /// <summary>
    /// Above one, this row is a switch port that several interfaces share (a hypervisor, a server
    /// with VLAN sub-interfaces) and its figures are the port's, as the map's hub node shows them.
    /// The interfaces behind it are not listed separately where that would count them twice.
    /// </summary>
    public int PortClientCount { get; init; }

    public bool IsPortGroup => PortClientCount > 1;

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name! : Ip ?? ClientMac;
}

/// <summary>The list for one instant or one window, before the card picks a view and takes ten.</summary>
public sealed record HogsResult
{
    public IReadOnlyList<HogRow> Rows { get; init; } = Array.Empty<HogRow>();

    /// <summary>The selected WAN(s)' rate at the instant; null when nothing could say.</summary>
    public double? WanDownBps { get; init; }
    public double? WanUpBps { get; init; }

    /// <summary>The console's expected speeds for those WANs, summed; null when none is known.</summary>
    public double? WanCapacityDownBps { get; init; }
    public double? WanCapacityUpBps { get; init; }

    /// <summary>True when the WAN split had to be estimated in either direction.</summary>
    public bool WanEstimated { get; init; }

    /// <summary>
    /// Seconds until the live baselines behind the WAN split have enough history to arm. Positive
    /// only in the first moments after the server starts, when unlearned rows attribute
    /// conservatively and device WAN rates can read low.
    /// </summary>
    public int WarmupSecondsRemaining { get; init; }

    /// <summary>Data mode: whether the LAN + WAN figures were read, or only the WAN ones.</summary>
    public bool IncludesLan { get; init; }

    public DateTime? At { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}
