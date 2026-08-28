using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Per-AP retry pacing for the redeploy supervisor: exponential backoff, an in-flight guard, and a
/// startup stagger.
///
/// All three are correctness requirements rather than tuning. Without backoff a permanently broken
/// AP is hammered forever; without the in-flight guard a slow deploy collects a second one behind
/// it on the next supervision tick; without the stagger a server restart opens an SSH session and a
/// file transfer to every AP on the site at the same instant.
/// </summary>
public sealed class ApAgentRetryPolicy
{
    /// <summary>A rebooting AP is back inside two minutes, so the first retries are cheap and quick.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    /// <summary>Ceiling on the doubling. A dead AP settles to four attempts an hour.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    /// <summary>Window the startup stagger spreads a site's APs across.</summary>
    public static readonly TimeSpan StaggerWindow = TimeSpan.FromMinutes(2);

    private sealed class RetryState
    {
        public int ConsecutiveFailures;
        public DateTime NextAttemptUtc = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<string, RetryState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The delay after a given number of consecutive failures: 30 s doubling to a 15 min cap.</summary>
    public static TimeSpan DelayForAttempt(int consecutiveFailures)
    {
        if (consecutiveFailures <= 1) return InitialDelay;

        // Shift rather than Math.Pow: the exponent is unbounded in principle, and a long-dead AP
        // must saturate at the cap instead of overflowing into a negative delay.
        var steps = Math.Min(consecutiveFailures - 1, 20);
        var ticks = InitialDelay.Ticks << steps;
        return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks(ticks);
    }

    /// <summary>Records a failure and returns when this AP may next be attempted.</summary>
    public DateTime RecordFailure(string deviceMac, DateTime nowUtc)
    {
        var state = _state.GetOrAdd(deviceMac, _ => new RetryState());
        lock (state)
        {
            state.ConsecutiveFailures++;
            state.NextAttemptUtc = nowUtc + DelayForAttempt(state.ConsecutiveFailures);
            return state.NextAttemptUtc;
        }
    }

    /// <summary>Clears the backoff for an AP that answered.</summary>
    public void RecordSuccess(string deviceMac)
    {
        if (!_state.TryGetValue(deviceMac, out var state)) return;
        lock (state)
        {
            state.ConsecutiveFailures = 0;
            state.NextAttemptUtc = DateTime.MinValue;
        }
    }

    /// <summary>Whether this AP's backoff has elapsed.</summary>
    public bool IsReady(string deviceMac, DateTime nowUtc)
        => !_state.TryGetValue(deviceMac, out var state) || nowUtc >= NextAttempt(state);

    /// <summary>When this AP may next be attempted, or null when it is not backing off.</summary>
    public DateTime? NextAttemptAt(string deviceMac)
    {
        if (!_state.TryGetValue(deviceMac, out var state)) return null;
        var next = NextAttempt(state);
        return next == DateTime.MinValue ? null : next;
    }

    /// <summary>Consecutive failures recorded for this AP.</summary>
    public int ConsecutiveFailures(string deviceMac)
        => _state.TryGetValue(deviceMac, out var state) ? Volatile.Read(ref state.ConsecutiveFailures) : 0;

    /// <summary>
    /// Claims this AP for a deploy, or returns null when one is already running for it. Dispose the
    /// claim to release it.
    /// </summary>
    public IDisposable? TryBeginWork(string deviceMac)
        => _inFlight.TryAdd(deviceMac, 0) ? new WorkClaim(this, deviceMac) : null;

    /// <summary>Whether work is currently running for this AP.</summary>
    public bool IsWorkInFlight(string deviceMac) => _inFlight.ContainsKey(deviceMac);

    /// <summary>
    /// A stable per-AP offset within <see cref="StaggerWindow"/>. Derived from the MAC so the same
    /// AP lands in the same slot across restarts and two APs cannot drift into the same one.
    /// </summary>
    public static TimeSpan StaggerOffset(string deviceMac) => StaggerOffset(deviceMac, StaggerWindow);

    /// <summary>A stable per-AP offset within a caller-chosen window.</summary>
    public static TimeSpan StaggerOffset(string deviceMac, TimeSpan window)
    {
        if (window <= TimeSpan.Zero) return TimeSpan.Zero;
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(deviceMac ?? ""));
        var slot = BitConverter.ToUInt32(hash, 0) % (uint)Math.Max(1, window.TotalSeconds);
        return TimeSpan.FromSeconds(slot);
    }

    /// <summary>Forgets an AP entirely - it left the site, or the operator opted it out.</summary>
    public void Forget(string deviceMac)
    {
        _state.TryRemove(deviceMac, out _);
        _inFlight.TryRemove(deviceMac, out _);
    }

    private static DateTime NextAttempt(RetryState state)
    {
        lock (state) return state.NextAttemptUtc;
    }

    private sealed class WorkClaim(ApAgentRetryPolicy owner, string deviceMac) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1) return;
            owner._inFlight.TryRemove(deviceMac, out _);
        }
    }
}
