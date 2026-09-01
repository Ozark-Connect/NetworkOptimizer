using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Runs the gateway agent install/upgrade one-liner on the site's gateway, streaming output
/// into the per-site run held by <see cref="GatewayAgentInstallState"/>.
///
/// The command runs DETACHED on the gateway (nohup into a log file plus an exit-code file)
/// and the server polls the log back over short SSH dials. Never a live exec channel: the
/// installer's final step restarts the agent, and on an agent-tunnel-routed site that tears
/// down the very tunnel a live channel would be riding - the detached run just keeps going
/// locally, and the polls retry through the few seconds the tunnel takes to come back. One
/// mechanism for every site, tunnel-routed or directly dialed.
///
/// The run method spans the whole execution, so the audit envelope the interceptor writes
/// carries the final exit code - and because everything it touches is singleton-owned or
/// plain objects, the run keeps going to completion even when the circuit that started it
/// is disposed mid-install.
/// </summary>
public class GatewayAgentInstallService : IGatewayAgentInstallService
{
    /// <summary>
    /// Overall cap per run - the ~100 MB agent binary is pulled from GitHub on the gateway's
    /// WAN. Deliberately no inactivity timeout: that download is a silent curl that can
    /// produce nothing for minutes.
    /// </summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long consecutive poll failures are tolerated before the run is declared lost.
    /// Generous on purpose: the agent restart at the end of the installer drops a tunnel
    /// for seconds, not minutes.
    /// </summary>
    public static readonly TimeSpan PollFailureGrace = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollSshTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Shown when a second start is refused while a run is active (never queued).</summary>
    public const string AlreadyRunningMessage =
        "A run is already in progress on this site's gateway - wait for it to finish.";

    // The detached run's files on the gateway. /tmp is tmpfs, so a reboot clears them, and
    // fixed names are safe because runs are serialized per site (one gateway per site).
    private const string RemoteLogFile = "/tmp/netopt-gateway-run.log";
    private const string RemoteExitFile = "/tmp/netopt-gateway-run.exit";
    private const string RemotePidFile = "/tmp/netopt-gateway-run.pid";

    // Poll-output fences: some gateways print a banner ahead of exec output, so the log is
    // read between markers rather than from the start of the reply.
    private const string LogMarker = "__NETOPT_LOG__";
    private const string ExitMarker = "__NETOPT_EXIT__:";

    private const string PollCommand =
        "echo " + LogMarker + "; cat " + RemoteLogFile + " 2>/dev/null; " +
        "printf '\\n" + ExitMarker + "%s\\n' \"$(cat " + RemoteExitFile + " 2>/dev/null)\"";

    // Kill the detached run's whole process group (set -m at start makes it a group leader),
    // falling back to the leader alone; ends in true so a missing pid file is not a failure.
    private const string KillCommand =
        "if [ -f " + RemotePidFile + " ]; then p=$(cat " + RemotePidFile + "); " +
        "kill -s TERM -- \"-$p\" 2>/dev/null; kill -s TERM \"$p\" 2>/dev/null; fi; true";

    private readonly GatewayAgentInstallState _state;
    private readonly GatewaySshRegistry _gatewaySsh;
    private readonly AgentServerUrlProvider _serverUrl;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<GatewayAgentInstallService> _logger;

    public GatewayAgentInstallService(
        GatewayAgentInstallState state,
        GatewaySshRegistry gatewaySsh,
        AgentServerUrlProvider serverUrl,
        IAuditContext auditContext,
        ILogger<GatewayAgentInstallService> logger)
    {
        _state = state;
        _gatewaySsh = gatewaySsh;
        _serverUrl = serverUrl;
        _auditContext = auditContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(string siteSlug)
    {
        if (_state.TryGetAvailability(siteSlug, out var cached))
            return Task.FromResult(cached);

        return _state.GetOrStartAvailabilityCheck(siteSlug, () => CheckAvailabilityAsync(siteSlug));
    }

    /// <inheritdoc />
    public Task<GatewayAgentInstallRun?> GetRunAsync(string siteSlug) =>
        Task.FromResult(_state.GetRun(siteSlug));

    /// <inheritdoc />
    public Task RunInstallAsync(string siteSlug, string enrollmentToken, Action<GatewayAgentInstallRun>? onStarted = null) =>
        RunAsync(siteSlug, GatewayAgentCommands.Install(_serverUrl.Url, enrollmentToken), isUpgrade: false, onStarted);

    /// <inheritdoc />
    public Task RunUpgradeAsync(string siteSlug, Action<GatewayAgentInstallRun>? onStarted = null) =>
        RunAsync(siteSlug, GatewayAgentCommands.Upgrade(_serverUrl.Url), isUpgrade: true, onStarted);

    /// <inheritdoc />
    public Task CancelRunAsync(string siteSlug)
    {
        var run = _state.GetRun(siteSlug);
        if (run is { Status: GatewayAgentInstallStatus.Running })
            run.UserCancellation.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The gate's configuration half, kept static and pure for the unit tests: whether the
    /// site is even a candidate before the dial is attempted. An unset server URL means the
    /// displayed command holds a placeholder no gateway could call back on. Agent-tunnel-routed
    /// sites are candidates like any other - the dial and the run both ride the tunnel.
    /// </summary>
    public static bool IsCandidate(bool serverUrlConfigured, GatewaySshSettings? settings) =>
        serverUrlConfigured
        && settings is { Enabled: true }
        && !string.IsNullOrEmpty(settings.Host)
        && settings.HasCredentials;

    /// <summary>
    /// Wraps the one-liner for a detached run: the payload is embedded verbatim, its output
    /// goes to the log file, its exit code to the exit file, and the pid file holds the
    /// group leader (set -m) so cancel can kill the whole tree.
    /// </summary>
    public static string BuildDetachedStart(string command)
    {
        var inner = command + "\necho $? > " + RemoteExitFile;
        var escaped = inner.Replace("'", "'\\''");
        return "rm -f " + RemoteLogFile + " " + RemoteExitFile + " " + RemotePidFile + "\n" +
               "set -m\n" +
               "nohup bash -c '" + escaped + "' </dev/null >" + RemoteLogFile + " 2>&1 &\n" +
               "echo $! >" + RemotePidFile;
    }

    /// <summary>
    /// Pulls the log text and exit code out of one poll reply. Null when the reply does not
    /// carry the markers (the poll command did not run). A null exit code means the run is
    /// still going.
    /// </summary>
    public static (string Log, int? ExitCode)? ParsePollOutput(string output)
    {
        var startIdx = output.IndexOf(LogMarker, StringComparison.Ordinal);
        var exitIdx = output.LastIndexOf(ExitMarker, StringComparison.Ordinal);
        if (startIdx < 0 || exitIdx < startIdx)
            return null;

        var logStart = output.IndexOf('\n', startIdx);
        if (logStart < 0 || logStart > exitIdx)
            return null;
        logStart += 1;

        // The poll's printf always contributes exactly one newline ahead of the exit marker,
        // so stripping one preserves whether the log itself ended with a newline.
        var logEnd = exitIdx;
        if (logEnd > logStart && output[logEnd - 1] == '\n')
            logEnd -= 1;
        if (logEnd > logStart && output[logEnd - 1] == '\r')
            logEnd -= 1;
        var log = logEnd > logStart ? output[logStart..logEnd] : "";

        var valueStart = exitIdx + ExitMarker.Length;
        var valueEnd = output.IndexOf('\n', valueStart);
        if (valueEnd < 0)
            valueEnd = output.Length;
        var value = output[valueStart..valueEnd].Trim();

        return (log, int.TryParse(value, out var code) ? code : null);
    }

    private async Task<bool> CheckAvailabilityAsync(string siteSlug)
    {
        try
        {
            var ssh = _gatewaySsh.GetFor(siteSlug);
            var settings = await ssh.GetSettingsAsync();
            if (!IsCandidate(!string.IsNullOrWhiteSpace(_serverUrl.Url), settings))
                return false;

            // The dial itself: a no-op command instead of TestConnectionAsync, which writes
            // its result back to the settings row on every success.
            var (ok, _) = await ssh.RunCommandAsync("true", DialTimeout);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gateway agent install availability check failed for site {Slug}", siteSlug);
            return false;
        }
    }

    private async Task RunAsync(string siteSlug, string command, bool isUpgrade, Action<GatewayAgentInstallRun>? onStarted)
    {
        var mode = isUpgrade ? "upgrade" : "install";
        var run = _state.StartRun(siteSlug, isUpgrade, AlreadyRunningMessage);
        onStarted?.Invoke(run);

        try
        {
            var ssh = _gatewaySsh.GetFor(siteSlug);
            _auditContext.SetTarget((await ssh.GetSettingsAsync()).Host);

            var (started, startError) = await ssh.RunCommandAsync(BuildDetachedStart(command), TimeSpan.FromSeconds(30));
            if (!started)
            {
                run.Append(startError + "\n");
                run.Complete(GatewayAgentInstallStatus.Failed, null, startError);
                return;
            }

            await PollToCompletionAsync(ssh, run);
        }
        catch (Exception ex)
        {
            // A run must never be left Running - it would block every future start for the site.
            run.Complete(GatewayAgentInstallStatus.Failed, null, ex.Message);
            throw;
        }
        finally
        {
            _auditContext.SetDetails(new { mode, status = run.Status.ToString(), exitCode = run.ExitCode });
            _logger.LogInformation(
                "Gateway agent {Mode} on site {Slug}: {Status} (exit code {ExitCode}). Transcript:\n{Transcript}",
                mode, siteSlug, run.Status, run.ExitCode?.ToString() ?? "n/a", run.Transcript);
        }
    }

    private async Task PollToCompletionAsync(GatewaySshService ssh, GatewayAgentInstallRun run)
    {
        var deadline = run.StartedAtUtc + RunTimeout;
        DateTime? failingSince = null;
        var lastError = "";
        var seen = 0;

        while (true)
        {
            if (run.CancelRequested || DateTime.UtcNow > deadline)
            {
                await KillDetachedRunAsync(ssh);
                run.Complete(
                    run.CancelRequested ? GatewayAgentInstallStatus.Canceled : GatewayAgentInstallStatus.Failed,
                    null,
                    run.CancelRequested ? null : $"Timed out after {RunTimeout.TotalMinutes:0} minutes.");
                return;
            }

            var (ok, output) = await ssh.RunCommandAsync(PollCommand, PollSshTimeout);
            var parsed = ok ? ParsePollOutput(output) : null;
            if (parsed is { } poll)
            {
                failingSince = null;
                if (poll.Log.Length > seen)
                {
                    run.Append(poll.Log[seen..]);
                    seen = poll.Log.Length;
                }
                if (poll.ExitCode is int code)
                {
                    run.Complete(
                        code == 0 ? GatewayAgentInstallStatus.Succeeded : GatewayAgentInstallStatus.Failed,
                        code, null);
                    return;
                }
            }
            else
            {
                // A tunnel dropping while the installer restarts the agent lands here for a
                // few polls; only a sustained outage fails the run.
                failingSince ??= DateTime.UtcNow;
                lastError = ok ? "unexpected poll reply" : output;
                if (DateTime.UtcNow - failingSince > PollFailureGrace)
                {
                    run.Complete(GatewayAgentInstallStatus.Failed, null,
                        $"Lost contact with the gateway: {lastError}");
                    return;
                }
            }

            await Task.Delay(PollInterval, CancellationToken.None);
        }
    }

    private async Task KillDetachedRunAsync(GatewaySshService ssh)
    {
        try
        {
            await ssh.RunCommandAsync(KillCommand, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort kill of the detached gateway run failed");
        }
    }
}
