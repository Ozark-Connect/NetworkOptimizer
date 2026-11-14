using System.Diagnostics;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Keeps the <c>traceroute</c> package installed on the gateway an agent runs on. UniFi OS ships only
/// BusyBox's applet, which probes one hop at a time and cannot finish a Path Localization trace inside
/// the agent's deadline, and a firmware upgrade can drop the package the installer added.
/// </summary>
internal static class GatewayTraceroute
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Installs the package when it is missing, retrying until it is there: at boot the WAN may not be up yet.
    /// Returns once <c>traceroute</c> is on the PATH. The probe executors pick it up on their next re-detection.
    /// </summary>
    public static async Task KeepInstalledAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !IsInstalled())
        {
            Console.WriteLine("traceroute is not installed - installing it with apt");
            var (ok, output) = await InstallAsync(ct);
            if (ok && IsInstalled())
            {
                Console.WriteLine("traceroute installed");
                return;
            }
            var reason = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            Console.WriteLine($"traceroute install failed, retrying in {RetryEvery.TotalMinutes:0} minutes: {reason ?? "no output"}");
            try { await Task.Delay(RetryEvery, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Whether a <c>traceroute</c> executable is on the PATH. BusyBox's applet is not one.</summary>
    internal static bool IsInstalled() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/sbin:/usr/bin:/sbin:/bin")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, "traceroute")));

    /// <summary>
    /// Runs apt in its own transient unit, outside the agent's cgroup, and never kills it: apt stopped
    /// mid-dpkg leaves dpkg interrupted, which blocks every later install on the gateway, UniFi's included.
    /// A stop or timeout here only stops waiting. A second run while one is going fails on the unit name.
    /// </summary>
    private static async Task<(bool Ok, string Output)> InstallAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("systemd-run")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in new[]
            {
                "--unit=netopt-traceroute-install", "--collect", "--wait", "--quiet",
                "--setenv=DEBIAN_FRONTEND=noninteractive",
                "/bin/sh", "-c", "apt-get update -qq && apt-get install -y -qq traceroute"
            })
                psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi);
            if (proc == null) return (false, "could not start systemd-run");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InstallTimeout);
            var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                return (false, ct.IsCancellationRequested ? "stopped" : $"still running after {InstallTimeout.TotalMinutes:0} minutes");
            }
            var output = await stdout + "\n" + await stderr;
            return proc.ExitCode == 0
                ? (true, output)
                : (false, $"{output.Trim()} (journalctl -u netopt-traceroute-install)");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
