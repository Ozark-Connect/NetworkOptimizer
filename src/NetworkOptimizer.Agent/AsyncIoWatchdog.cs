using System.Net;
using System.Net.Sockets;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Self-heal for a wedged .NET async socket engine, seen on a UniFi gateway's
/// vendor kernel (2026-07-30): epoll_wait slept through a wakeup, so every
/// async socket completion stopped being delivered while the kernel kept
/// completing the underlying work. Timers, sync I/O, and child processes all
/// keep running, so the agent looks alive while the tunnel, heartbeats, and
/// probe pipe reads starve forever on connects the kernel already finished.
///
/// Detection is a loopback canary: an async connect to our own listener on
/// 127.0.0.1. That connect cannot time out for network reasons, so several
/// consecutive in-process timeouts prove the engine is dead - a real WAN or
/// server outage never trips it. The engine's event loop is process-global,
/// so the only remedy is a process restart: the watchdog persists the result
/// backlog and exits non-zero for the unit's Restart=always to relaunch.
/// </summary>
public static class AsyncIoWatchdog
{
    /// <summary>Consecutive canary failures before declaring the engine dead.</summary>
    public const int FailuresBeforeRestart = 3;

    /// <summary>EX_SOFTWARE: distinguishes a watchdog restart from a crash in journal/systemd status.</summary>
    public const int ExitCode = 70;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CanaryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs for the life of the process. <paramref name="persistState"/> is
    /// invoked (best-effort) right before the restart exit; it must use only
    /// sync I/O, since async I/O is exactly what is broken at that point.
    /// </summary>
    public static async Task RunAsync(Action persistState, CancellationToken ct)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(FailuresBeforeRestart + 1);
        var endpoint = (IPEndPoint)listener.LocalEndPoint!;

        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(Interval, ct);

            var completed = await CanaryConnectAsync(endpoint, ct);
            DrainBacklog(listener);
            if (ct.IsCancellationRequested)
                return;
            if (completed)
            {
                failures = 0;
                continue;
            }

            failures++;
            Console.Error.WriteLine(
                $"Async I/O canary: loopback connect did not complete in {CanaryTimeout.TotalSeconds:0}s ({failures}/{FailuresBeforeRestart})");
            if (failures < FailuresBeforeRestart)
                continue;

            Console.Error.WriteLine(
                "Async socket engine is wedged (the kernel completes loopback connects this process never observes); " +
                "exiting so systemd relaunches the agent");
            try
            {
                persistState();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"State persist before watchdog restart failed: {ex.Message}");
            }
            Environment.Exit(ExitCode);
        }
    }

    /// <summary>
    /// True when the async connect COMPLETED - success or failure both prove
    /// the engine is delivering completions. False only when nothing arrived
    /// within the timeout, which on loopback means the delivery path is dead.
    /// </summary>
    private static async Task<bool> CanaryConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task connect;
        try
        {
            connect = socket.ConnectAsync(endpoint);
        }
        catch
        {
            socket.Dispose();
            return true;
        }

        var winner = await Task.WhenAny(connect, Task.Delay(CanaryTimeout, ct));
        if (winner == connect)
        {
            try { await connect; } catch { }
            socket.Dispose();
            return true;
        }

        // Timed out (or shutdown cancelled the delay - the caller re-checks ct).
        // Observe the completion that may never fire, and close the kernel-side
        // connection so it doesn't linger.
        _ = connect.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        socket.Dispose();
        return false;
    }

    /// <summary>
    /// Accepts and discards the canary's connections on the SYNC path, which
    /// works even when the async engine is dead - otherwise kernel-established
    /// canary connects would fill the listen backlog and fail future canaries
    /// for the wrong reason.
    /// </summary>
    private static void DrainBacklog(Socket listener)
    {
        try
        {
            while (listener.Poll(0, SelectMode.SelectRead))
                listener.Accept().Dispose();
        }
        catch
        {
        }
    }
}
