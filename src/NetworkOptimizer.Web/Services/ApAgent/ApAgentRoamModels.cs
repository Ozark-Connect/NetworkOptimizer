using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>One AP's own 802.11k neighbor report element, as the agent reports it.</summary>
public sealed class ApAgentNeighborReport
{
    [JsonPropertyName("vap")] public string Vap { get; set; } = "";
    [JsonPropertyName("bssid")] public string Bssid { get; set; } = "";
    [JsonPropertyName("ssid")] public string Ssid { get; set; } = "";

    /// <summary>Hex neighbor report element, passed through untouched as a BTM candidate.</summary>
    [JsonPropertyName("element")] public string Element { get; set; } = "";
}

/// <summary>The agent's <c>/neighbors</c> reply.</summary>
public sealed class ApAgentNeighborsPayload
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("neighbors")] public List<ApAgentNeighborReport> Neighbors { get; set; } = new();
}

/// <summary>The body of a BSS transition request.</summary>
public sealed class ApAgentTransitionRequest
{
    [JsonPropertyName("candidates")] public List<string> Candidates { get; set; } = new();
    [JsonPropertyName("duration_tbtt")] public int DurationTbtt { get; set; }
    [JsonPropertyName("abridged")] public bool Abridged { get; set; }
}

/// <summary>What the agent reports back about a transition request it sent.</summary>
public sealed class ApAgentTransitionResult
{
    [JsonPropertyName("mac")] public string Mac { get; set; } = "";
    [JsonPropertyName("vap")] public string Vap { get; set; } = "";
    [JsonPropertyName("candidates")] public int Candidates { get; set; }
}

/// <summary>
/// Outcome of asking a client to move. Success means the request was delivered, never that the
/// client complied: 802.11v is a request, and where it lands arrives later as a roam event.
/// </summary>
/// <param name="Success">Whether the BTM frame was sent.</param>
/// <param name="Message">What happened, for the operator.</param>
/// <param name="FromApName">Access point the client was on.</param>
/// <param name="CandidateCount">How many candidates were offered.</param>
public readonly record struct ApAgentRoamResult(
    bool Success,
    string Message,
    string? FromApName = null,
    int CandidateCount = 0)
{
    public static ApAgentRoamResult Fail(string message) => new(false, message);
}
