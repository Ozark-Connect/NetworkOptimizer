using System.Text.RegularExpressions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.HealthChecks;

/// <summary>
/// The remedial commands a health check can run, and what each device type supports. Every
/// remedy is a fixed command with at most one argument, and the argument is validated before it
/// is interpolated: the check's own command is the user's to write, the remedy is not.
/// </summary>
public static class HealthCheckRemedies
{
    // A service or process name. Same idea as GatewayDiagnosticsParser.IsValidInterfaceName.
    private static readonly Regex ArgumentPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._@:-]{0,63}$", RegexOptions.Compiled);

    /// <summary>True when the argument is safe to place in a shell command.</summary>
    public static bool IsValidArgument(string? arg) =>
        !string.IsNullOrWhiteSpace(arg) && ArgumentPattern.IsMatch(arg);

    /// <summary>Whether the remedy needs an argument at all.</summary>
    public static bool NeedsArgument(HealthCheckRemedy remedy) =>
        remedy is HealthCheckRemedy.RestartService or HealthCheckRemedy.KillProcess;

    /// <summary>
    /// Whether a device type can run the remedy. Only a UniFi OS gateway has systemd; access
    /// points and switches run busybox, where <c>pkill</c> and <c>reboot</c> still exist.
    /// </summary>
    public static bool SupportedOn(HealthCheckRemedy remedy, DeviceType deviceType) => remedy switch
    {
        HealthCheckRemedy.None => true,
        HealthCheckRemedy.RestartService => deviceType == DeviceType.Gateway,
        HealthCheckRemedy.KillProcess => true,
        HealthCheckRemedy.RebootDevice => true,
        _ => false,
    };

    /// <summary>The command to run, or null when the remedy is none or its argument is invalid.</summary>
    public static string? BuildCommand(HealthCheckRemedy remedy, string? arg)
    {
        switch (remedy)
        {
            case HealthCheckRemedy.RestartService:
                return IsValidArgument(arg) ? $"systemctl restart {arg}" : null;
            case HealthCheckRemedy.KillProcess:
                return IsValidArgument(arg) ? $"pkill -x {arg}" : null;
            case HealthCheckRemedy.RebootDevice:
                // Backgrounded with a short delay so the SSH session gets its exit back before the
                // box goes away; otherwise the run reports a dropped connection as a failure.
                return "( sleep 2; reboot ) >/dev/null 2>&1 &";
            default:
                return null;
        }
    }

    /// <summary>
    /// Waits for a restarted unit to settle, prints its state, and exits 0 only when it is active.
    /// While systemd retries a failed start (<c>Restart=</c>), the unit reads "activating", so the
    /// loop waits that out. 80 reads 10 s apart stay inside the remedy's SSH timeout.
    /// </summary>
    public static string? BuildActiveCheckCommand(string? unit) =>
        IsValidArgument(unit)
            ? "for i in $(seq 1 80); do s=$(systemctl is-active " + unit + "); case \"$s\" in " +
              "active) echo active; exit 0;; failed|inactive) echo \"$s\"; exit 1;; esac; sleep 10; done; " +
              "echo \"$s\"; exit 1"
            : null;

    /// <summary>What the remedy did, in one clause for alerts and the event mark.</summary>
    public static string Describe(HealthCheckRemedy remedy, string? arg) => remedy switch
    {
        HealthCheckRemedy.RestartService => $"restarted the {arg} service",
        HealthCheckRemedy.KillProcess => $"killed {arg}",
        HealthCheckRemedy.RebootDevice => "rebooted the device",
        _ => "took no action",
    };

    /// <summary>The remedy's name in the editor.</summary>
    public static string Label(HealthCheckRemedy remedy) => remedy switch
    {
        HealthCheckRemedy.RestartService => "Restart a service",
        HealthCheckRemedy.KillProcess => "Kill a process",
        HealthCheckRemedy.RebootDevice => "Reboot the device",
        _ => "Nothing",
    };
}
