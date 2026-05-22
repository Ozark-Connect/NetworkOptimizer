using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Monitoring.Probes;

/// <summary>
/// A managed ICMP traceroute that works on Windows, Linux, and macOS without depending on
/// platform binaries (tracert.exe, traceroute). Implements RFC 1393 by repeatedly sending
/// ICMP echoes with increasing TTL and recording each TimeExceeded reply.
///
/// Used as the primary path on Windows MSI installs (where tracert.exe output is a
/// different shape from Linux/busybox and would require its own parser — see spec open
/// question #10). Also serves as the implementation fallback on any platform where the
/// native `traceroute` binary isn't present.
///
/// **Linux capability requirement.** This implementation calls the
/// <c>Ping.SendPingAsync(host, timeout, buffer, options)</c> overload, which passes a
/// custom payload + TTL via PingOptions. That overload uses a raw ICMP socket and
/// requires CAP_NET_RAW on the calling process — the unprivileged
/// <c>IPPROTO_ICMP</c> socket Linux exposes to non-root users does *not* support custom
/// PingOptions and throws <see cref="PlatformNotSupportedException"/>. The Network
/// Optimizer Docker container runs as a non-root user without CAP_NET_RAW, so on Linux
/// the <see cref="LocalProbeExecutor"/> prefers the native <c>traceroute -I</c> binary
/// (which is setuid / has its own capabilities) for ICMP and only falls back to this
/// managed implementation when the binary is missing.
/// </summary>
public class ManagedTraceroute
{
    private readonly ILogger _logger;

    public ManagedTraceroute(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<TracerouteResult> RunAsync(
        ProbeTarget target,
        ProbeVantage vantage,
        int maxHops = 30,
        TimeSpan? perHopTimeout = null,
        int probesPerHop = 3,
        CancellationToken ct = default)
    {
        var timeoutMs = (int)(perHopTimeout?.TotalMilliseconds ?? 2000);
        var hops = new List<TraceHop>(maxHops);
        var buffer = new byte[32];
        new Random().NextBytes(buffer);

        IPAddress? targetIp = null;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target.Address, ct);
            targetIp = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                       ?? addresses.FirstOrDefault();
            if (targetIp == null)
            {
                return new TracerouteResult
                {
                    Target = target,
                    Vantage = vantage,
                    ModeUsed = ProbeMode.Icmp,
                    Hops = hops,
                    Reached = false,
                    ErrorMessage = $"Could not resolve {target.Address}",
                    Timestamp = DateTime.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            return new TracerouteResult
            {
                Target = target,
                Vantage = vantage,
                ModeUsed = ProbeMode.Icmp,
                Hops = hops,
                Reached = false,
                ErrorMessage = $"DNS resolution failed: {ex.Message}",
                Timestamp = DateTime.UtcNow
            };
        }

        bool reached = false;
        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            if (ct.IsCancellationRequested) break;

            var rtts = new List<double>();
            string? replyAddress = null;
            int responses = 0;
            IPStatus lastStatus = IPStatus.Unknown;

            for (int probe = 0; probe < probesPerHop; probe++)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    using var ping = new Ping();
                    var options = new PingOptions(ttl, dontFragment: true);
                    var sw = Stopwatch.StartNew();
                    var reply = await ping.SendPingAsync(targetIp, timeoutMs, buffer, options);
                    sw.Stop();
                    lastStatus = reply.Status;

                    if (reply.Status == IPStatus.TtlExpired ||
                        reply.Status == IPStatus.Success ||
                        reply.Status == IPStatus.TimeExceeded)
                    {
                        responses++;
                        rtts.Add(sw.Elapsed.TotalMilliseconds);
                        replyAddress ??= reply.Address?.ToString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Managed traceroute probe failed at TTL {Ttl}", ttl);
                }
            }

            hops.Add(new TraceHop
            {
                HopNumber = ttl,
                Address = replyAddress,
                Hostname = null,
                RttMinMs = rtts.Count > 0 ? rtts.Min() : null,
                RttAvgMs = rtts.Count > 0 ? rtts.Average() : null,
                RttMaxMs = rtts.Count > 0 ? rtts.Max() : null,
                Probes = probesPerHop,
                Responses = responses
            });

            if (lastStatus == IPStatus.Success || (replyAddress != null && IPAddress.TryParse(replyAddress, out var addr) && addr.Equals(targetIp)))
            {
                reached = true;
                break;
            }
        }

        return new TracerouteResult
        {
            Target = target,
            Vantage = vantage,
            ModeUsed = ProbeMode.Icmp,
            Hops = hops,
            Reached = reached,
            Timestamp = DateTime.UtcNow
        };
    }
}
