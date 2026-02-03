namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// Roaming topology data from /v2/api/site/{site}/wifi-connectivity/roaming/topology
/// Shows aggregate roaming statistics between APs
/// </summary>
public class RoamingTopology
{
    /// <summary>Clients that have roamed</summary>
    public List<RoamingClient> Clients { get; set; } = new();

    /// <summary>Roaming statistics between AP pairs</summary>
    public List<RoamingEdge> Edges { get; set; } = new();

    /// <summary>APs with roaming data</summary>
    public List<RoamingVertex> Vertices { get; set; } = new();
}

/// <summary>
/// A client that has participated in roaming
/// </summary>
public class RoamingClient
{
    /// <summary>Client MAC address</summary>
    public string Mac { get; set; } = string.Empty;

    /// <summary>Client name</summary>
    public string? Name { get; set; }
}

/// <summary>
/// Roaming statistics between two APs (bidirectional edge)
/// </summary>
public class RoamingEdge
{
    /// <summary>First AP MAC</summary>
    public string Endpoint1Mac { get; set; } = string.Empty;

    /// <summary>Second AP MAC</summary>
    public string Endpoint2Mac { get; set; } = string.Empty;

    /// <summary>Stats for roaming from AP1 to AP2</summary>
    public RoamingDirectionStats Endpoint1ToEndpoint2 { get; set; } = new();

    /// <summary>Stats for roaming from AP2 to AP1</summary>
    public RoamingDirectionStats Endpoint2ToEndpoint1 { get; set; } = new();

    /// <summary>Top clients roaming between these APs</summary>
    public List<ClientRoamingStats> TopRoamingClients { get; set; } = new();

    /// <summary>Total roam attempts between these APs (both directions)</summary>
    public int TotalRoamAttempts { get; set; }

    /// <summary>Total successful roams between these APs (both directions)</summary>
    public int TotalSuccessfulRoams { get; set; }

    /// <summary>Calculated success rate</summary>
    public double SuccessRate => TotalRoamAttempts > 0
        ? (double)TotalSuccessfulRoams / TotalRoamAttempts * 100
        : 100;
}

/// <summary>
/// Roaming statistics for one direction between two APs
/// </summary>
public class RoamingDirectionStats
{
    /// <summary>Number of roam attempts</summary>
    public int RoamAttempts { get; set; }

    /// <summary>Number of successful roams</summary>
    public int SuccessfulRoams { get; set; }

    /// <summary>Number of fast roaming (802.11r) events</summary>
    public int FastRoaming { get; set; }

    /// <summary>Number triggered by min RSSI threshold</summary>
    public int TriggeredByMinimalRssi { get; set; }

    /// <summary>Number triggered by roaming assistant</summary>
    public int TriggeredByRoamingAssistant { get; set; }

    /// <summary>Calculated success rate</summary>
    public double SuccessRate => RoamAttempts > 0
        ? (double)SuccessfulRoams / RoamAttempts * 100
        : 100;

    /// <summary>Percentage using fast roaming</summary>
    public double FastRoamingPct => SuccessfulRoams > 0
        ? (double)FastRoaming / SuccessfulRoams * 100
        : 0;
}

/// <summary>
/// Per-client roaming statistics
/// </summary>
public class ClientRoamingStats
{
    /// <summary>Client MAC</summary>
    public string Mac { get; set; } = string.Empty;

    /// <summary>Total roam attempts</summary>
    public int RoamAttempts { get; set; }

    /// <summary>Successful roams</summary>
    public int SuccessfulRoams { get; set; }

    /// <summary>Calculated success rate</summary>
    public double SuccessRate => RoamAttempts > 0
        ? (double)SuccessfulRoams / RoamAttempts * 100
        : 100;
}

/// <summary>
/// An AP in the roaming topology (vertex)
/// </summary>
public class RoamingVertex
{
    /// <summary>AP MAC address</summary>
    public string Mac { get; set; } = string.Empty;

    /// <summary>AP model code</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>AP name</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Radio information</summary>
    public List<RoamingRadioInfo> Radios { get; set; } = new();
}

/// <summary>
/// Radio info in roaming context
/// </summary>
public class RoamingRadioInfo
{
    /// <summary>Channel number</summary>
    public int Channel { get; set; }

    /// <summary>Radio band code (ng, na, 6e)</summary>
    public string RadioBand { get; set; } = string.Empty;

    /// <summary>Band as enum</summary>
    public RadioBand Band => RadioBandExtensions.FromUniFiCode(RadioBand);

    /// <summary>Utilization percentage</summary>
    public int UtilizationPercentage { get; set; }
}
