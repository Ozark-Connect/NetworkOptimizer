using System.Diagnostics;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Reports devices that tried to reach an outbound-blocked address. The enforcer's LOG rule writes each
/// drop (rate limited) to the kernel log; this reads it from the shared <see cref="KernelLogFollower"/> and
/// sends one summary a minute, per device, address, and port. The drops happen ahead of UniFi Network's
/// rules, so nothing else records them.
/// </summary>
public sealed class GatewayBlocklistHitReporter
{
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMinutes(1);
    private const int MaxKeys = 500;

    private readonly object _gate = new();
    private Dictionary<(string Client, string Dest, string Proto, int Port), GatewayBlocklistHit> _window = new();

    /// <summary>Live path: the current tunnel's TrySend. Null drops the summary.</summary>
    public volatile Func<AgentMessage, bool>? LiveSend;

    public GatewayBlocklistHitReporter(KernelLogFollower kernelLog) =>
        kernelLog.Subscribe(line =>
        {
            if (GatewayDropLog.Parse(line, GatewayBlocklistEnforcer.OutLogPrefix) is { } drop)
                Record(drop);
        });

    public Task RunAsync(CancellationToken ct) => FlushLoopAsync(ct);

    // The neighbor table (both families), read only when a summary has hits to send.
    private static Dictionary<string, string> Neighbors()
    {
        try
        {
            var psi = new ProcessStartInfo("ip", "neigh show") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(psi);
            if (process == null) return new();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return GatewayDropLog.ParseNeighbors(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new();
        }
    }

    private void Record(GatewayDropLog drop)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var key = (drop.ClientIp, drop.DestIp, drop.Protocol, drop.DestPort);
        lock (_gate)
        {
            if (_window.TryGetValue(key, out var hit))
            {
                hit.Count++;
                hit.LastUnixMs = now;
            }
            else if (_window.Count < MaxKeys)
            {
                _window[key] = new GatewayBlocklistHit
                {
                    ClientIp = drop.ClientIp, DestIp = drop.DestIp, Protocol = drop.Protocol, DestPort = drop.DestPort,
                    Count = 1, FirstUnixMs = now, LastUnixMs = now,
                };
            }
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(FlushEvery, ct); }
            catch (OperationCanceledException) { return; }
            Dictionary<(string, string, string, int), GatewayBlocklistHit> window;
            lock (_gate)
            {
                if (_window.Count == 0) continue;
                window = _window;
                _window = new();
            }
            var neighbors = Neighbors();
            foreach (var hit in window.Values)
                hit.ClientMac = neighbors.GetValueOrDefault(hit.ClientIp, "");
            var message = new GatewayBlocklistHits();
            message.Hits.AddRange(window.Values);
            LiveSend?.Invoke(new AgentMessage { GatewayBlocklistHits = message });
        }
    }
}
