using System.Text;

namespace NetworkOptimizer.Web.Services;

/// <summary>Lifecycle of one "Run It for Me" gateway agent install/upgrade run.</summary>
public enum GatewayAgentInstallStatus
{
    Running,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>
/// Live state of one "Run It for Me" gateway agent install/upgrade: the transcript as it
/// streams in, the final status, and the script's exit code. The latest run per site is held
/// by <see cref="GatewayAgentInstallState"/> for the lifetime of the process, so a page
/// reopened mid-run or after completion can still show the transcript.
/// </summary>
public class GatewayAgentInstallRun
{
    private readonly StringBuilder _transcript = new();
    private readonly object _lock = new();

    public GatewayAgentInstallRun(string siteSlug, bool isUpgrade)
    {
        SiteSlug = siteSlug;
        IsUpgrade = isUpgrade;
    }

    /// <summary>The site whose gateway the run targets.</summary>
    public string SiteSlug { get; }

    /// <summary>True for the upgrade one-liner (no token), false for a first-time install.</summary>
    public bool IsUpgrade { get; }

    /// <summary>When the run started.</summary>
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>Where the run is in its lifecycle.</summary>
    public GatewayAgentInstallStatus Status { get; private set; } = GatewayAgentInstallStatus.Running;

    /// <summary>The script's exit code, when it ran to an exit at all.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>Failure summary when the run ended without a clean exit (SSH drop, timeout).</summary>
    public string? Error { get; private set; }

    /// <summary>Raised on every transcript append and once on completion. May fire on any thread.</summary>
    public event Action? Updated;

    /// <summary>Cancels the SSH channel when the user clicks Cancel.</summary>
    internal CancellationTokenSource UserCancellation { get; } = new();

    internal bool CancelRequested => UserCancellation.IsCancellationRequested;

    /// <summary>Everything the command has written so far (stdout and stderr interleaved).</summary>
    public string Transcript
    {
        get { lock (_lock) return _transcript.ToString(); }
    }

    internal void Append(string chunk)
    {
        lock (_lock) _transcript.Append(chunk);
        Updated?.Invoke();
    }

    internal void Complete(GatewayAgentInstallStatus status, int? exitCode, string? error)
    {
        Status = status;
        ExitCode = exitCode;
        Error = error;
        Updated?.Invoke();
    }
}
