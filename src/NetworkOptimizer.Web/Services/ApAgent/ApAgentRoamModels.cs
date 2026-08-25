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

/// <summary>
/// What the operator is asking for, which decides the candidate list.
///
/// Asked rather than inferred: a client that lands somewhere unhelpful is corrected by clicking
/// again, and guessing wrong would send it to the far side of the site.
/// </summary>
public enum ApAgentRoamIntent
{
    /// <summary>Move to a different access point. Candidates are the other access points.</summary>
    AccessPoint,

    /// <summary>Move to a different band on the same access point, best band first.</summary>
    Band,
}

/// <summary>The body of a BSS transition request.</summary>
public sealed class ApAgentTransitionRequest
{
    [JsonPropertyName("candidates")] public List<string> Candidates { get; set; } = new();
    [JsonPropertyName("duration_tbtt")] public int DurationTbtt { get; set; }
    [JsonPropertyName("abridged")] public bool Abridged { get; set; }

    /// <summary>
    /// Blocks rejoining the access point it left, for this long, once it has actually gone. 0 is off.
    /// Agents older than binary version 8 ignore it, which costs the bounce-back guard and nothing else.
    /// </summary>
    [JsonPropertyName("ban_ms")] public int BanMs { get; set; }
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
