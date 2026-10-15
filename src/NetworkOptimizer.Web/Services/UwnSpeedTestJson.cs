using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

// JSON deserialization models matching the uwnspeedtest binary output (snake_case). Shared by the
// server/agent runner and the gateway runner.

internal sealed class WanSpeedTestResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public WanMetadata? Metadata { get; set; }
    public WanLatency? Latency { get; set; }
    public WanLoss? IdleLoss { get; set; }
    public WanThroughput? Download { get; set; }
    public WanThroughput? Upload { get; set; }
    public int Streams { get; set; }
    public int DurationSeconds { get; set; }
}

internal sealed class WanMetadata
{
    public string Ip { get; set; } = "";
    public string Colo { get; set; } = "";
    public string Country { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public List<string>? ServerIps { get; set; }
}

internal sealed class WanLatency
{
    public double UnloadedMs { get; set; }
    public double JitterMs { get; set; }
}

internal sealed class WanThroughput
{
    public double Bps { get; set; }
    public long Bytes { get; set; }
    public double LoadedLatencyMs { get; set; }
    public double LoadedJitterMs { get; set; }
    public WanLoss? Loss { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

/// <summary>
/// A packet loss probe burst. For the "tcp_connect" method, a connect that failed or paid a SYN
/// retransmit counts as one lost packet.
/// </summary>
internal sealed class WanLoss
{
    public string Method { get; set; } = "";
    public int Sent { get; set; }
    public int Retried { get; set; }
    public int Failed { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    public int Lost => Retried + Failed;
}

internal static class WanLossMapper
{
    /// <summary>
    /// Pools probe bursts (one per WAN on a parallel gateway test) and writes the idle, download,
    /// and upload loss onto the result. A phase with no probes stays null.
    /// </summary>
    public static void Apply(Iperf3Result result, IReadOnlyCollection<WanSpeedTestResult> runs)
    {
        (result.IdleLossPercent, result.IdleLossProbes) = Pool(runs.Select(r => r.IdleLoss));
        (result.DownloadLossPercent, result.DownloadLossProbes) = Pool(runs.Select(r => r.Download?.Loss));
        (result.UploadLossPercent, result.UploadLossProbes) = Pool(runs.Select(r => r.Upload?.Loss));
    }

    private static (double? Percent, int? Probes) Pool(IEnumerable<WanLoss?> bursts)
    {
        int sent = 0, lost = 0;
        foreach (var burst in bursts)
        {
            if (burst is not { Sent: > 0 }) continue;
            sent += burst.Sent;
            lost += burst.Lost;
        }
        return sent == 0 ? (null, null) : (Math.Round(100.0 * lost / sent, 2), sent);
    }
}
