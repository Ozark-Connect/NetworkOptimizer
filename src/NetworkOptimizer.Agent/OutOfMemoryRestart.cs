using Grpc.Core;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Self-heal for an agent whose GC heap stops granting allocations while the process stays up.
/// The sampling loops and the tunnel catch their exceptions and carry on, so without this an
/// OutOfMemoryException repeats until someone restarts the unit. A burst of them restarts the
/// agent the way <see cref="AsyncIoWatchdog"/> does: spool the backlog, exit non-zero, and let
/// the unit's Restart=always relaunch it. The first failure of a burst and the last one before
/// the exit each log the stack and the GC's own view of the heap, so the cause stays diagnosable.
/// </summary>
public static class OutOfMemoryRestart
{
    /// <summary>Out-of-memory failures inside <see cref="Window"/> that trigger the restart.</summary>
    public const int FailuresBeforeRestart = 5;

    /// <summary>EX_OSERR: tells this restart apart from the watchdog's (70) and a crash.</summary>
    public const int ExitCode = 71;

    // A conntrack pass fails every 2 s once the heap is stuck, so 5 in 2 min is reached in about
    // 10 s. One stray failure, such as a single oversized allocation, ages out without a restart.
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private static readonly Burst Recent = new(Window);
    private static int _exiting;

    /// <summary>Counts failures inside a sliding window. Thread-safe.</summary>
    internal sealed class Burst(TimeSpan window)
    {
        private readonly Queue<DateTime> _times = new();

        /// <summary>Records one failure at <paramref name="now"/>; returns how many fall inside the window.</summary>
        public int Record(DateTime now)
        {
            lock (_times)
            {
                while (_times.Count > 0 && now - _times.Peek() > window) _times.Dequeue();
                _times.Enqueue(now);
                return _times.Count;
            }
        }
    }

    /// <summary>Spools the result backlog before the exit. Set once at startup; sync I/O only.</summary>
    public static Action? PersistState { get; set; }

    /// <summary>
    /// Records <paramref name="ex"/> if it is an out-of-memory failure; anything else is ignored.
    /// The caller keeps its own one-line log either way.
    /// </summary>
    /// <param name="restartNow">
    /// Restart on this failure, without waiting for a burst: for a path that runs minutes apart and has no
    /// retry, such as the gateway blocklist apply, where a burst never comes and a failure leaves the sets to drain.
    /// </param>
    public static void Report(string source, Exception ex, bool restartNow = false)
    {
        if (!IsOutOfMemory(ex)) return;

        var count = Recent.Record(DateTime.UtcNow);
        if (count == 1 && !restartNow)
            Console.Error.WriteLine(Describe($"Out of memory in {source} (first in {Window.TotalMinutes:0} min)", ex));
        if ((!restartNow && count < FailuresBeforeRestart) || Interlocked.Exchange(ref _exiting, 1) == 1) return;

        Console.Error.WriteLine(Describe(restartNow
            ? $"Out of memory in {source}; exiting so systemd relaunches the agent"
            : $"Out of memory in {source} ({count} in {Window.TotalMinutes:0} min); exiting so systemd relaunches the agent", ex));
        try
        {
            PersistState?.Invoke();
        }
        catch (Exception persistEx)
        {
            Console.Error.WriteLine($"State persist before out-of-memory restart failed: {persistEx.Message}");
        }
        Environment.Exit(ExitCode);
    }

    /// <summary>
    /// True for an OutOfMemoryException anywhere in the chain, including one gRPC wraps in an
    /// RpcException (the tunnel's "Error reading next message" carries it as the debug exception).
    /// </summary>
    public static bool IsOutOfMemory(Exception? ex)
    {
        for (var depth = 0; ex != null && depth < 8; depth++)
        {
            switch (ex)
            {
                case OutOfMemoryException:
                    return true;
                case RpcException rpc:
                    if (IsOutOfMemory(rpc.Status.DebugException)) return true;
                    if (rpc.Status.Detail?.Contains(nameof(OutOfMemoryException), StringComparison.Ordinal) == true) return true;
                    break;
                case AggregateException agg:
                    if (agg.InnerExceptions.Any(IsOutOfMemory)) return true;
                    break;
            }
            ex = ex.InnerException;
        }
        return false;
    }

    /// <summary>The exception with its stack, the GC's last-collection view, and the process's memory.</summary>
    private static string Describe(string headline, Exception ex)
    {
        var lines = new List<string> { headline, ex.ToString() };
        if (ex is RpcException { Status.DebugException: { } inner })
            lines.Add("gRPC debug exception: " + inner);

        try
        {
            var gc = GC.GetGCMemoryInfo();
            lines.Add($"GC: heap {Mb(gc.HeapSizeBytes)}, committed {Mb(gc.TotalCommittedBytes)}, " +
                      $"fragmented {Mb(gc.FragmentedBytes)}, available {Mb(gc.TotalAvailableMemoryBytes)}, " +
                      $"memory load {Mb(gc.MemoryLoadBytes)} of high {Mb(gc.HighMemoryLoadThresholdBytes)}, " +
                      $"pinned objects {gc.PinnedObjectsCount}, last GC #{gc.Index} gen{gc.Generation} " +
                      $"{(gc.Compacted ? "compacted" : "not compacted")}");
            string[] names = ["gen0", "gen1", "gen2", "loh", "poh"];
            var gens = gc.GenerationInfo;
            var genParts = new List<string>();
            for (var i = 0; i < Math.Min(names.Length, gens.Length); i++)
                genParts.Add($"{names[i]} {Mb(gens[i].SizeAfterBytes)}/{Mb(gens[i].FragmentationAfterBytes)}");
            lines.Add("GC generations after last GC (size/fragmented): " + string.Join(", ", genParts));
            lines.Add($"GC counts: gen0 {GC.CollectionCount(0)}, gen1 {GC.CollectionCount(1)}, gen2 {GC.CollectionCount(2)}; " +
                      $"allocated since start {Mb(GC.GetTotalAllocatedBytes())}");
            lines.Add("GC config: " + string.Join(", ",
                GC.GetConfigurationVariables().OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        }
        catch (Exception gcEx)
        {
            lines.Add($"GC info unavailable: {gcEx.Message}");
        }

        lines.Add($"Process: page size {Environment.SystemPageSize}, cpus {Environment.ProcessorCount}, {ProcStatus()}");
        return string.Join(Environment.NewLine + "  ", lines);
    }

    private static string ProcStatus()
    {
        try
        {
            var wanted = new[] { "VmPeak", "VmSize", "VmHWM", "VmRSS", "RssAnon", "VmSwap", "Threads" };
            var status = File.ReadLines("/proc/self/status")
                .Where(l => wanted.Any(w => l.StartsWith(w + ":", StringComparison.Ordinal)))
                .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+", " "));
            var maps = File.ReadLines("/proc/self/maps").Count();
            return string.Join(", ", status) + $", mappings {maps}";
        }
        catch (Exception ex)
        {
            return $"/proc unavailable: {ex.Message}";
        }
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.#} MB";
}
