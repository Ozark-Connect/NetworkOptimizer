using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;

namespace NetworkOptimizer.Agent;

/// <summary>
/// The site's latency/loss probe engine. The server pushes the site's
/// monitoring targets over the tunnel; this runner probes each on its own
/// cadence (2 s scheduler tick, per-target intervals, bounded concurrency,
/// mirroring the server's latency tier) and enqueues every result into the
/// store-and-forward <see cref="ResultBuffer"/>. Runs for the life of the
/// agent process: probing continues through tunnel outages - which is exactly
/// when latency/loss data matters most - with the buffer holding the backlog
/// and the last pushed target set staying in effect until the server
/// re-pushes on reconnect.
/// </summary>
public sealed class ProbeRunner
{
    private readonly Action<AgentMessage> _send;
    private readonly string? _defaultSourceIp;
    private readonly LocalProbeExecutor _executor = new(NullLogger<LocalProbeExecutor>.Instance);
    private readonly ConcurrentDictionary<string, DateTime> _lastProbed = new();
    private volatile IReadOnlyList<ProbeTargetSpec> _targets = [];

    /// <param name="defaultSourceIp">
    /// Source address every probe binds to unless the target spec carries its
    /// own. This is the probe-only multi-WAN deployment knob: the gateway
    /// policy-routes this source IP out a specific WAN.
    /// </param>
    public ProbeRunner(Action<AgentMessage> send, string? defaultSourceIp = null)
    {
        _send = send;
        _defaultSourceIp = string.IsNullOrEmpty(defaultSourceIp) ? null : defaultSourceIp;
    }

    /// <summary>Replaces the target set. Full replacement, matching the ProbeConfig contract.</summary>
    public void UpdateConfig(ProbeConfig config)
    {
        _targets = config.Targets.ToList();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Startup grace, mirroring the server latency tier's 20s initial delay
        // (MonitoringCollectionAgent.RunTierAsync). At agent process start every target
        // is "due" immediately, and probing the instant the process comes up catches
        // the network still settling (routes, source-IP binds, the target device itself
        // rebooting alongside) and records a false loss/latency spike right at the
        // start boundary. Wait for it to settle before the first tick, so no boundary
        // sample is taken. Tunnel reconnects no longer restart this runner, so no
        // grace (or gap) applies there - probing runs continuously across outages.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
        catch (OperationCanceledException) { return; }

        using var concurrency = new SemaphoreSlim(4);
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var due = _targets.Where(t => IsDue(t, now)).ToList();
            if (due.Count > 0)
                await Task.WhenAll(due.Select(t => ProbeAsync(t, concurrency, ct)));
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private bool IsDue(ProbeTargetSpec spec, DateTime now)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(2, spec.PollIntervalSeconds));
        return !_lastProbed.TryGetValue(spec.TargetId, out var last) || now - last >= interval;
    }

    private async Task ProbeAsync(ProbeTargetSpec spec, SemaphoreSlim concurrency, CancellationToken ct)
    {
        await concurrency.WaitAsync(ct);
        try
        {
            _lastProbed[spec.TargetId] = DateTime.UtcNow;

            var mode = Enum.TryParse<ProbeMode>(spec.ProbeMode, ignoreCase: true, out var parsed)
                ? parsed
                : ProbeMode.Icmp;
            var target = new ProbeTarget(
                spec.Address,
                mode,
                spec.Port > 0 ? spec.Port : null,
                string.IsNullOrEmpty(spec.SourceIp) ? _defaultSourceIp : spec.SourceIp);

            var ping = await _executor.PingAsync(
                target,
                count: Math.Max(3, Math.Min(spec.PingCount, 20)),
                perPingTimeout: TimeSpan.FromSeconds(2),
                ct: ct);
            if (ct.IsCancellationRequested) return;

            var result = new ProbeResult
            {
                TargetId = spec.TargetId,
                TimestampUnixMs = new DateTimeOffset(ping.Timestamp).ToUnixTimeMilliseconds(),
                Success = ping.Success,
                Sent = ping.Sent,
                Received = ping.Received,
                LossPercent = ping.LossPercent,
            };
            if (ping.RttMinMs.HasValue) result.RttMinMs = ping.RttMinMs.Value;
            if (ping.RttAvgMs.HasValue) result.RttAvgMs = ping.RttAvgMs.Value;
            if (ping.RttMaxMs.HasValue) result.RttMaxMs = ping.RttMaxMs.Value;
            if (ping.JitterMs.HasValue) result.JitterMs = ping.JitterMs.Value;

            _send(new AgentMessage { ProbeResults = new ProbeResultBatch { Results = { result } } });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Probe {spec.TargetId} failed: {ex.Message}");
        }
        finally
        {
            concurrency.Release();
        }
    }
}
