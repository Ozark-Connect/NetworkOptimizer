using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Runs the gateway agent install/upgrade one-liner over the site's existing gateway SSH
/// plumbing, streaming output into the per-site run held by
/// <see cref="GatewayAgentInstallState"/>. The run method spans the whole execution, so the
/// audit envelope the interceptor writes carries the final exit code - and because everything
/// it touches is singleton-owned or plain objects, the run keeps going to completion even
/// when the circuit that started it is disposed mid-install.
/// </summary>
public class GatewayAgentInstallService : IGatewayAgentInstallService
{
    /// <summary>
    /// Overall cap per run - the ~100 MB agent binary is pulled from GitHub on the gateway's
    /// WAN. Deliberately no inactivity timeout: that download is a silent curl that can
    /// produce nothing for minutes.
    /// </summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Shown when a second start is refused while a run is active (never queued).</summary>
    public const string AlreadyRunningMessage =
        "A run is already in progress on this site's gateway - wait for it to finish.";

    private readonly GatewayAgentInstallState _state;
    private readonly GatewaySshRegistry _gatewaySsh;
    private readonly SiteTunnelRouting _tunnelRouting;
    private readonly AgentServerUrlProvider _serverUrl;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<GatewayAgentInstallService> _logger;

    public GatewayAgentInstallService(
        GatewayAgentInstallState state,
        GatewaySshRegistry gatewaySsh,
        SiteTunnelRouting tunnelRouting,
        AgentServerUrlProvider serverUrl,
        IAuditContext auditContext,
        ILogger<GatewayAgentInstallService> logger)
    {
        _state = state;
        _gatewaySsh = gatewaySsh;
        _tunnelRouting = tunnelRouting;
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
    /// site is even a candidate before the dial is attempted. Tunnel-routed sites are out of
    /// v1 - their copy-paste command remains the path - and an unset server URL means the
    /// displayed command holds a placeholder no gateway could call back on.
    /// </summary>
    public static bool IsCandidate(bool serverUrlConfigured, bool routedViaAgent, GatewaySshSettings? settings) =>
        serverUrlConfigured
        && !routedViaAgent
        && settings is { Enabled: true }
        && !string.IsNullOrEmpty(settings.Host)
        && settings.HasCredentials;

    private async Task<bool> CheckAvailabilityAsync(string siteSlug)
    {
        try
        {
            var ssh = _gatewaySsh.GetFor(siteSlug);
            var settings = await ssh.GetSettingsAsync();
            if (!IsCandidate(
                    !string.IsNullOrWhiteSpace(_serverUrl.Url),
                    await _tunnelRouting.IsViaAgentAsync(siteSlug),
                    settings))
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(run.UserCancellation.Token);
        timeout.CancelAfter(RunTimeout);

        try
        {
            var ssh = _gatewaySsh.GetFor(siteSlug);
            _auditContext.SetTarget((await ssh.GetSettingsAsync()).Host);

            var result = await ssh.RunCommandStreamingAsync(command, run.Append, RunTimeout, timeout.Token);

            if (result.Success)
            {
                run.Complete(GatewayAgentInstallStatus.Succeeded, result.ExitCode, null);
            }
            else
            {
                // Exit -1 is our own sentinel for "never ran to an exit" (dial refused, SSH
                // dropped mid-run); the Error then says why and belongs in the transcript.
                var exitCode = result.ExitCode >= 0 ? result.ExitCode : (int?)null;
                if (exitCode == null && !string.IsNullOrWhiteSpace(result.Error))
                    run.Append((run.Transcript.Length > 0 ? "\n" : "") + result.Error + "\n");
                run.Complete(GatewayAgentInstallStatus.Failed, exitCode, exitCode == null ? result.Error : null);
            }
        }
        catch (OperationCanceledException)
        {
            run.Complete(
                run.CancelRequested ? GatewayAgentInstallStatus.Canceled : GatewayAgentInstallStatus.Failed,
                null,
                run.CancelRequested ? null : $"Timed out after {RunTimeout.TotalMinutes:0} minutes.");
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
}
