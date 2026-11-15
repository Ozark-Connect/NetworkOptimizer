using System.Diagnostics;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Relays the gateway's outbound CyberSecure (IPS) alerts to the server the moment they are logged, so
/// Automatic Blocking can block the remote end on every sharing site in seconds. Follows the threat log
/// with tail -F: the file appears with the first alert after boot and is rotated by size.
/// </summary>
public sealed class IpsAlertFollower
{
    // A burst past this many remote addresses a minute is left to the console's threat log, so junk in the
    // file can never flood the block lists.
    private const int MaxRemotesPerMinute = 10;
    private static readonly TimeSpan HomeNetEvery = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _homeNet = [];
    private DateTime _homeNetAt = DateTime.MinValue;
    private DateTime _windowStart = DateTime.MinValue;
    private int _windowCount;
    private readonly Dictionary<IpsAlertLine.Reject, int> _rejected = new();
    private DateTime _rejectLoggedAt = DateTime.MinValue;

    /// <summary>Live path: the current tunnel's TrySend. Null drops the alert (the console's log still has it).</summary>
    public volatile Func<AgentMessage, bool>? LiveSend;

    /// <summary>Whether this gateway runs UniFi's IPS log pipeline (the threat log's syslog-ng config).</summary>
    public static bool Available() =>
        OperatingSystem.IsLinux() && File.Exists("/etc/syslog-ng/conf.d/threat_log.conf");

    public Task RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Follow(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Follow(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // -F waits for the file and reopens it on rotation; -n 0 never replays old alerts.
                var psi = new ProcessStartInfo("tail", ["-F", "-n", "0", IpsAlertLine.LogPath])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                };
                using var process = Process.Start(psi);
                if (process == null) return;
                using var stop = ct.Register(() => { try { process.Kill(); } catch (InvalidOperationException) { } });
                string? line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    try { Handle(line, DateTime.UtcNow); }
                    catch (Exception ex) { Console.Error.WriteLine($"IPS alerts: a line failed ({ex.Message})"); }
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine("IPS alerts: tail not found, outbound IPS alerts are not relayed");
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"IPS alerts: follow failed ({ex.Message}), retrying");
            }
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        }
    }

    internal void Handle(string line, DateTime now)
    {
        var alert = IpsAlertLine.Parse(line, HomeNet(now), new DateTimeOffset(now, TimeSpan.Zero), out var reject);
        if (alert == null)
        {
            CountReject(reject, now);
            return;
        }
        if (!Admit(alert.RemoteIp, now)) return;
        Console.WriteLine($"IPS alerts: {alert.ClientIp} -> {alert.RemoteIp} \"{alert.Signature}\" relayed");
        LiveSend?.Invoke(new AgentMessage { IpsAlert = alert });
    }

    // One alert per remote address a minute goes out (the server needs only the first), and at most
    // MaxRemotesPerMinute addresses a minute.
    internal bool Admit(string remote, DateTime now)
    {
        lock (_gate)
        {
            foreach (var stale in _recent.Where(kv => now - kv.Value > TimeSpan.FromMinutes(1)).Select(kv => kv.Key).ToList())
                _recent.Remove(stale);
            if (_recent.ContainsKey(remote)) return false;
            if (now - _windowStart > TimeSpan.FromMinutes(1))
            {
                _windowStart = now;
                _windowCount = 0;
            }
            if (_windowCount >= MaxRemotesPerMinute)
            {
                if (_windowCount++ == MaxRemotesPerMinute)
                    Console.Error.WriteLine($"IPS alerts: over {MaxRemotesPerMinute} addresses this minute, the rest are left to the console");
                return false;
            }
            _windowCount++;
            _recent[remote] = now;
            return true;
        }
    }

    // HOME_NET follows the networks IPS is turned on for, so it is re-read every few minutes.
    private IReadOnlyList<string> HomeNet(DateTime now)
    {
        if (now - _homeNetAt < HomeNetEvery) return _homeNet;
        _homeNetAt = now;
        try
        {
            _homeNet = IpsAlertLine.ParseHomeNet(File.Exists(IpsAlertLine.HomeNetPath) ? File.ReadAllText(IpsAlertLine.HomeNetPath) : null);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return _homeNet;
    }

    // Inbound alerts are the common case and expected; anything else rejected is summarized hourly.
    private void CountReject(IpsAlertLine.Reject reject, DateTime now)
    {
        if (reject == IpsAlertLine.Reject.NotOutbound) return;
        _rejected[reject] = _rejected.GetValueOrDefault(reject) + 1;
        if (now - _rejectLoggedAt < TimeSpan.FromHours(1)) return;
        _rejectLoggedAt = now;
        Console.Error.WriteLine($"IPS alerts: lines not relayed: {string.Join(", ", _rejected.Select(kv => $"{kv.Key} {kv.Value}"))}");
        _rejected.Clear();
    }
}
