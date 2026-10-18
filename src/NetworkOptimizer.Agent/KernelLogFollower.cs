using System.Diagnostics;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Follows the kernel log through journalctl and hands each line to every subscriber. One follower serves
/// the gateway blocklist's drop reports and WAN Flood Protection's table-full events, so the gateway runs
/// one journalctl, not one per reader.
/// </summary>
public sealed class KernelLogFollower
{
    private readonly List<Action<string>> _subscribers = new();

    /// <summary>Adds a subscriber. Call before <see cref="RunAsync"/>; subscribers run on the follower's thread.</summary>
    public void Subscribe(Action<string> onLine)
    {
        lock (_subscribers) _subscribers.Add(onLine);
    }

    public Task RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Follow(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Follow(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // stderr is not redirected: nothing would read it, and a full pipe would stall journalctl.
                var psi = new ProcessStartInfo("journalctl", "-k -f -n 0 -o cat")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                using var process = Process.Start(psi);
                if (process == null) return;
                using var stop = ct.Register(() => { try { process.Kill(); } catch (InvalidOperationException) { } });
                string? line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    Action<string>[] subscribers;
                    lock (_subscribers) subscribers = _subscribers.ToArray();
                    foreach (var subscriber in subscribers)
                    {
                        try { subscriber(line); }
                        catch (Exception ex) { Console.Error.WriteLine($"Kernel log: a reader failed on a line ({ex.Message})"); }
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine("Kernel log: journalctl not found, gateway drop reports and table-full events are unavailable");
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Kernel log: follow failed ({ex.Message}), retrying");
            }
            // journalctl exited (journald restart); start it again after a pause.
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        }
    }
}
