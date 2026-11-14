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

    private static async Task<(bool Ok, string Output)> InstallAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("apt-get update -qq && apt-get install -y -qq traceroute");
            psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
            using var proc = Process.Start(psi);
            if (proc == null) return (false, "could not start /bin/sh");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InstallTimeout);
            var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (false, ct.IsCancellationRequested ? "stopped" : $"timed out after {InstallTimeout.TotalMinutes:0} minutes");
            }
            return (proc.ExitCode == 0, await stdout + "\n" + await stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
