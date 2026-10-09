using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.Monitoring.HealthChecks;

/// <summary>One run of a check's command: what came back and what was read out of it.</summary>
public sealed class HealthCheckRunResult
{
    /// <summary>The SSH round trip happened and the command ran. False carries <see cref="Error"/>.</summary>
    public bool Ran { get; init; }

    public string? Error { get; init; }

    /// <summary>The command's own output, marker stripped.</summary>
    public string Output { get; init; } = string.Empty;

    public int? ExitCode { get; init; }

    /// <summary>True when the output matched the not-applicable rule.</summary>
    public bool NotApplicable { get; init; }

    /// <summary>The parsed value, or null when the parser found nothing.</summary>
    public double? Value { get; init; }

    /// <summary>Whether the condition holds on this sample (the check is failing).</summary>
    public bool Failing { get; init; }

    public DateTime At { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Runs a check's command on a device over the right SSH path and reads the result. Shared by the
/// per-site runner and the editor's Test button so both see exactly the same thing.
/// </summary>
public static class HealthCheckExecutor
{
    /// <summary>
    /// Runs the check once, on the SSH route <see cref="DeviceSshRouter"/> picks for the device.
    /// </summary>
    public static async Task<HealthCheckRunResult> RunAsync(
        HealthCheckDefinition check,
        DeviceSshTarget target,
        DeviceSshRouter router,
        CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(check.TimeoutSeconds, 5, 120));
        var wrapped = HealthCheckEvaluation.WrapCommand(check.Command);

        if (NoAddress(target))
            return new HealthCheckRunResult { Ran = false, Error = NoAddressError };

        bool success;
        string raw;
        try
        {
            (success, raw) = await router.RunAsync(target, wrapped, timeout, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthCheckRunResult { Ran = false, Error = $"The command did not finish within {timeout.TotalSeconds:0} seconds." };
        }
        catch (Exception ex)
        {
            return new HealthCheckRunResult { Ran = false, Error = ex.Message };
        }

        var (output, exitCode) = HealthCheckEvaluation.Unwrap(raw);

        // The wrapper always exits 0, so a failed run here is the SSH layer refusing or the box
        // not answering; its text is the reason.
        if (!success && exitCode == null)
            return new HealthCheckRunResult { Ran = false, Error = string.IsNullOrWhiteSpace(raw) ? "SSH command failed." : raw.Trim() };

        if (HealthCheckEvaluation.IsNotApplicable(output, check.NotApplicablePattern))
            return new HealthCheckRunResult { Ran = true, Output = output, ExitCode = exitCode, NotApplicable = true };

        var value = HealthCheckEvaluation.Parse(output, exitCode, check.Parser, check.ParserArg);
        return new HealthCheckRunResult
        {
            Ran = true,
            Output = output,
            ExitCode = exitCode,
            Value = value,
            Failing = value.HasValue && HealthCheckEvaluation.ConditionHolds(value.Value, check.Operator, check.Threshold),
        };
    }

    /// <summary>Runs a remedy command on the device. Returns the SSH layer's verdict and text.</summary>
    public static async Task<(bool Success, string Output)> RunRemedyAsync(
        string command,
        DeviceSshTarget target,
        DeviceSshRouter router,
        CancellationToken ct)
    {
        if (NoAddress(target)) return (false, NoAddressError);
        try
        {
            return await router.RunAsync(target, command, RemedyTimeout, ct);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// <c>systemctl restart</c> blocks until the unit is up, and unifi.service allows 15 minutes
    /// (<c>TimeoutStartSec=15min</c>). A shorter wait reports a restart that is still going as failed.
    /// </summary>
    private static readonly TimeSpan RemedyTimeout = TimeSpan.FromMinutes(15);

    private const string NoAddressError = "The site reports no address for this device.";

    /// <summary>The site gateway is dialed at its configured host, so only other devices need an address.</summary>
    private static bool NoAddress(DeviceSshTarget target) =>
        target.Role != DeviceType.Gateway && string.IsNullOrWhiteSpace(target.Host);
}
