namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// One WAN flood episode on a site's gateway, as its WAN Flood Protection reported it: when it ran, how
/// hard it hit the receive path, and what was dropped or limited. Written by the On-Site Agent's incident
/// reports and updated in place by <see cref="IncidentId"/> as the episode changes.
/// </summary>
public class WanFloodIncident
{
    public int Id { get; set; }

    /// <summary>The agent's id for the episode; repeated reports of one episode update one row.</summary>
    public string IncidentId { get; set; } = string.Empty;

    /// <summary>The WAN interfaces the protection covered, comma-separated.</summary>
    public string Wan { get; set; } = string.Empty;

    /// <summary>"flood" until attributed, then "udp" or "syn"; "conntrack" for conntrack table pressure.</summary>
    public string Kind { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    /// <summary>Null while the episode runs.</summary>
    public DateTime? EndedAt { get; set; }

    public int PeakUnsolicitedPps { get; set; }

    public int PeakSoftirqPercent { get; set; }

    /// <summary>The receive CPU the flood loaded most, or -1.</summary>
    public int HotCpu { get; set; } = -1;

    public int PeakConntrackPercent { get; set; }

    /// <summary>Packets the receive backlog dropped during the episode.</summary>
    public long SoftnetDropped { get; set; }

    /// <summary>A per-source table filled during the episode, so its rules were removed for a while.</summary>
    public bool TableFull { get; set; }

    /// <summary>JSON list of the drops and limits held during the episode.</summary>
    public string ActionsJson { get; set; } = "[]";

    /// <summary>"agent" (On-Site Agent on the gateway).</summary>
    public string Delivery { get; set; } = "agent";

    /// <summary>"enforce" or "detect".</summary>
    public string Mode { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
