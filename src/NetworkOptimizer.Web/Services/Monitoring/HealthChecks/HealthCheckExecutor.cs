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
    /// Runs the check once. A gateway uses the console's SSH credentials, everything else the
    /// shared device credentials, the same split <c>DeviceRebootProbe</c> makes.
    /// </summary>
    public static async Task<HealthCheckRunResult> RunAsync(
        HealthCheckDefinition check,
        DeviceType deviceType,
        string? host,
        IGatewaySshService gatewaySsh,
        IUniFiSshService deviceSsh,
        CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(check.TimeoutSeconds, 5, 120));
        var wrapped = HealthCheckEvaluation.WrapCommand(check.Command);

        bool success;
        string raw;
        try
        {
            if (deviceType == DeviceType.Gateway)
            {
                (success, raw) = await gatewaySsh.RunCommandAsync(wrapped, timeout, ct);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(host))
                    return new HealthCheckRunResult { Ran = false, Error = "The site reports no address for this device." };
                (success, raw) = await deviceSsh.RunCommandAsync(host, wrapped, null, timeout, ct);
            }
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
        DeviceType deviceType,
        string? host,
        IGatewaySshService gatewaySsh,
        IUniFiSshService deviceSsh,
        CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(60);
        try
        {
            if (deviceType == DeviceType.Gateway)
                return await gatewaySsh.RunCommandAsync(command, timeout, ct);
            if (string.IsNullOrWhiteSpace(host))
                return (false, "The site reports no address for this device.");
            return await deviceSsh.RunCommandAsync(host, command, null, timeout, ct);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
