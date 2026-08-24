namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>What the server found when it last looked at an AP's agent.</summary>
public enum ApAgentState
{
    /// <summary>Nothing has been observed yet.</summary>
    Unknown,

    /// <summary>The AP's architecture has no AP Agent build.</summary>
    Unsupported,

    /// <summary>The console reports the AP itself is down. Not an agent problem.</summary>
    ApOffline,

    /// <summary>Answering, current, and probing on schedule.</summary>
    Healthy,

    /// <summary>Answering and healthy, but running an older contract version than this server ships.</summary>
    OutOfDate,

    /// <summary>Answering, but its probes stopped running.</summary>
    Wedged,

    /// <summary>Answering with 401. The agent runs; the server holds the wrong token.</summary>
    Unauthorized,

    /// <summary>The AP refused the connection: it is reachable and nothing is listening.</summary>
    NotListening,

    /// <summary>The connection was dropped in the path rather than refused. Almost always a policy block.</summary>
    Filtered,

    /// <summary>Reached, but the answer was not one the server can act on.</summary>
    Unhealthy,
}

/// <summary>What the server should do about an <see cref="ApAgentState"/>.</summary>
public enum ApAgentAction
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>Push the binary again. The only action that costs an SSH session and a file transfer.</summary>
    Redeploy,

    /// <summary>Push the token and restart. The binary on the AP is fine.</summary>
    RepushConfig,

    /// <summary>Restart the process already on the AP. Do not re-transfer.</summary>
    RestartInPlace,

    /// <summary>A newer agent is available. Upgrade on the normal cycle, not urgently.</summary>
    Upgrade,

    /// <summary>Report the path problem. Redeploying cannot fix a blocked path, and SSH is blocked too.</summary>
    SurfacePathProblem,

    /// <summary>Wait for the AP to come back. Do not burn SSH attempts on a device that is down.</summary>
    Wait,
}

/// <summary>How a connection to an AP's agent ended, normalized away from transport specifics.</summary>
public enum ApAgentReach
{
    /// <summary>No attempt was made.</summary>
    NotAttempted,

    /// <summary>An HTTP response came back, whatever its status.</summary>
    Answered,

    /// <summary>
    /// TCP RST. The packet reached the AP and nothing was listening, which is the one outcome that
    /// justifies a redeploy.
    /// </summary>
    Refused,

    /// <summary>
    /// Silently dropped. Something in the path is filtering, so SSH is likely blocked as well and a
    /// redeploy would fail the same way.
    /// </summary>
    TimedOut,

    /// <summary>No route to the AP at all.</summary>
    Unreachable,

    /// <summary>The failure did not identify itself. Treated as unactionable rather than guessed at.</summary>
    Unknown,
}

/// <summary>
/// One observation of an AP's agent, as the classifier consumes it. Deliberately transport-free so
/// the discrimination table can be exercised without a socket.
/// </summary>
/// <param name="Reach">How the connection ended.</param>
/// <param name="HttpStatus">Status code when <paramref name="Reach"/> is Answered.</param>
/// <param name="DeviceOnline">Whether the console currently reports the AP as connected.</param>
/// <param name="SupportedArchitecture">False when the AP's architecture has no build.</param>
/// <param name="Health">Parsed /health body, when one came back.</param>
/// <param name="ExpectedBinaryVersion">The contract version this server ships.</param>
/// <param name="Detail">Free-text reason carried through to the operator.</param>
public sealed record ApAgentObservation(
    ApAgentReach Reach,
    int? HttpStatus = null,
    bool DeviceOnline = true,
    bool SupportedArchitecture = true,
    ApAgentHealthPayload? Health = null,
    int ExpectedBinaryVersion = 0,
    string? Detail = null);

/// <summary>The fields of the AP Agent's GET /health the server acts on.</summary>
/// <param name="Version">Agent release version.</param>
/// <param name="BinaryVersion">Agent contract version.</param>
/// <param name="LastProbeRun">When the agent last re-ran its capability probes.</param>
/// <param name="CollectedAt">The agent's own clock when it built the response.</param>
/// <param name="Degraded">Whether any probe is unavailable.</param>
/// <param name="Unavailable">Names of the probes that did not resolve.</param>
public sealed record ApAgentHealthPayload(
    string? Version,
    int BinaryVersion,
    DateTime LastProbeRun,
    DateTime CollectedAt,
    bool Degraded,
    IReadOnlyList<string> Unavailable);

/// <summary>The classifier's verdict: what the AP is doing, and what to do about it.</summary>
/// <param name="State">The condition observed.</param>
/// <param name="Action">The single action that condition warrants.</param>
/// <param name="Detail">Operator-facing reason.</param>
public sealed record ApAgentAssessment(ApAgentState State, ApAgentAction Action, string Detail);

/// <summary>
/// Everything one SSH round trip reports about an AP. Mirrors WanSteerDeploymentService's status
/// probe: an AP is a slow SSH target, so the fields are gathered in a single delimited command.
/// </summary>
public sealed class ApAgentSshStatus
{
    /// <summary>True when the SSH command itself succeeded. Every other field is meaningless without it.</summary>
    public bool Reachable { get; set; }

    /// <summary>Result of <c>uname -m</c>, e.g. "armv7l".</summary>
    public string? Machine { get; set; }

    /// <summary>Board name from /etc/board.info.</summary>
    public string? Model { get; set; }

    /// <summary>Firmware string from /usr/lib/version.</summary>
    public string? Firmware { get; set; }

    /// <summary>Whether an AP Agent build exists for <see cref="Machine"/>.</summary>
    public bool SupportedArchitecture { get; set; }

    /// <summary>Whether the agent binary is present and executable on the AP.</summary>
    public bool BinaryDeployed { get; set; }

    /// <summary>Whether the architecture-gating wrapper is present and executable.</summary>
    public bool WrapperDeployed { get; set; }

    /// <summary>Whether an agent process is running.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Whether procd is available to supervise the agent on this AP.</summary>
    public bool ProcdAvailable { get; set; }

    /// <summary>Agent release version reported by the deployed binary.</summary>
    public string? Version { get; set; }

    /// <summary>Agent contract version reported by the deployed binary.</summary>
    public int? DeployedBinaryVersion { get; set; }

    /// <summary>MD5 of the deployed binary, used to skip a transfer that would change nothing.</summary>
    public string? BinaryMd5 { get; set; }

    /// <summary>Why the probe failed, when it did.</summary>
    public string? Error { get; set; }
}

/// <summary>Outcome of a deploy, restart, or removal.</summary>
/// <param name="Success">Whether the operation completed.</param>
/// <param name="Error">Why it did not, when it did not.</param>
/// <param name="State">The AP's state after the operation.</param>
public sealed record ApAgentOperationResult(bool Success, string? Error = null, ApAgentState State = ApAgentState.Unknown)
{
    /// <summary>A successful operation, with the state it left the AP in.</summary>
    public static ApAgentOperationResult Ok(ApAgentState state = ApAgentState.Healthy) => new(true, null, state);

    /// <summary>A failed operation, with the reason and the state it left the AP in.</summary>
    public static ApAgentOperationResult Fail(string error, ApAgentState state = ApAgentState.Unknown) => new(false, error, state);
}

/// <summary>One access point's row in the AP Telemetry fleet table.</summary>
public sealed class ApAgentFleetEntry
{
    /// <summary>The AP's MAC, normalized to lower-case colon form.</summary>
    public string DeviceMac { get; set; } = "";

    /// <summary>The AP's name as the console reports it.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>The AP's product name.</summary>
    public string Model { get; set; } = "";

    /// <summary>The AP's management address.</summary>
    public string Host { get; set; } = "";

    /// <summary>Whether the console reports the AP as connected right now.</summary>
    public bool DeviceOnline { get; set; }

    /// <summary>False when the operator has opted this AP out.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The AP's last observed agent state.</summary>
    public ApAgentState State { get; set; } = ApAgentState.Unknown;

    /// <summary>What that state warrants.</summary>
    public ApAgentAction RecommendedAction { get; set; } = ApAgentAction.None;

    /// <summary>Operator-facing reason behind the state.</summary>
    public string? Detail { get; set; }

    /// <summary>Machine architecture last read over SSH.</summary>
    public string? Architecture { get; set; }

    /// <summary>Agent release version last seen running.</summary>
    public string? DeployedVersion { get; set; }

    /// <summary>Agent contract version last seen running.</summary>
    public int? DeployedBinaryVersion { get; set; }

    /// <summary>When the binary was last pushed.</summary>
    public DateTime? LastDeployedAt { get; set; }

    /// <summary>When the agent was last reached successfully.</summary>
    public DateTime? LastHealthyAt { get; set; }

    /// <summary>Last recorded failure.</summary>
    public string? LastError { get; set; }

    /// <summary>When the supervisor will next act on this AP, while it is backing off.</summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>Consecutive failures behind the current backoff delay.</summary>
    public int ConsecutiveFailures { get; set; }
}

/// <summary>One probe from an AP Agent's GET /capabilities: a telemetry source, resolved by behavior.</summary>
/// <param name="Name">Stable probe name, e.g. "wlanconfig". The agent keys on these across releases.</param>
/// <param name="Available">Whether the probe resolved on this access point.</param>
/// <param name="Fatal">True for the one probe the agent cannot run without.</param>
/// <param name="Detail">What the probe found, or why it failed.</param>
/// <param name="Degrades">What the agent loses while this probe is unavailable.</param>
public sealed record ApAgentCapabilityProbe(
    string Name,
    bool Available,
    bool Fatal,
    string? Detail,
    string? Degrades);

/// <summary>
/// An AP Agent's capability report, from its GET /capabilities. The agent probes what the access
/// point can provide at startup, so this is per-AP: sources vary by model and firmware.
/// </summary>
/// <param name="Version">Agent release version.</param>
/// <param name="Model">The AP's model as the agent read it.</param>
/// <param name="Firmware">The AP's firmware string.</param>
/// <param name="Vaps">Serving VAP interfaces the agent enumerated.</param>
/// <param name="Radios">Radio interfaces the agent enumerated.</param>
/// <param name="Probes">Every telemetry source the agent probed.</param>
/// <param name="ProbedAt">When the agent last ran its probes.</param>
public sealed record ApAgentCapabilityReport(
    string? Version,
    string? Model,
    string? Firmware,
    IReadOnlyList<string> Vaps,
    IReadOnlyList<string> Radios,
    IReadOnlyList<ApAgentCapabilityProbe> Probes,
    DateTime ProbedAt);
