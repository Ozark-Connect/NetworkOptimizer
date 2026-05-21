using System.Globalization;
using System.Text.RegularExpressions;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Monitoring.Probes;

/// <summary>
/// Parses ping output from both standard Linux (iputils) and BusyBox. Tolerant of partial
/// or unusual output — never throws. Returns null fields when a value couldn't be parsed
/// rather than fabricating.
/// </summary>
public static class PingOutputParser
{
    // iputils summary:   "5 packets transmitted, 5 received, 0% packet loss, time 4007ms"
    // busybox summary:   "5 packets transmitted, 5 packets received, 0% packet loss"
    private static readonly Regex SummaryRegex = new(
        @"(?<sent>\d+)\s+packets?\s+transmitted,\s+(?<recv>\d+)\s+(?:packets?\s+)?received,\s+(?<loss>\d+(?:\.\d+)?)%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // iputils:  "rtt min/avg/max/mdev = 1.234/2.345/3.456/0.456 ms"
    // busybox:  "round-trip min/avg/max = 1.234/2.345/3.456 ms"
    private static readonly Regex RttRegex = new(
        @"(?:rtt|round-trip)\s+min/avg/max(?:/mdev)?\s*=\s*(?<min>\d+(?:\.\d+)?)/(?<avg>\d+(?:\.\d+)?)/(?<max>\d+(?:\.\d+)?)(?:/(?<mdev>\d+(?:\.\d+)?))?\s*ms",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Per-reply line for jitter computation when summary lacks mdev:
    //   "64 bytes from 1.1.1.1: icmp_seq=1 ttl=58 time=3.45 ms"
    private static readonly Regex PerReplyRegex = new(
        @"time\s*=\s*(?<rtt>\d+(?:\.\d+)?)\s*ms",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PingProbeResult Parse(
        string output,
        ProbeTarget target,
        ProbeVantage vantage,
        int requestedCount,
        DateTime? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new PingProbeResult
            {
                Target = target,
                Vantage = vantage,
                Sent = requestedCount,
                Received = 0,
                Timestamp = timestamp ?? DateTime.UtcNow,
                ErrorMessage = "No output from ping",
                RawOutput = output
            };
        }

        var summary = SummaryRegex.Match(output);
        var rtt = RttRegex.Match(output);

        int sent = requestedCount;
        int received = 0;
        if (summary.Success)
        {
            sent = int.Parse(summary.Groups["sent"].Value, CultureInfo.InvariantCulture);
            received = int.Parse(summary.Groups["recv"].Value, CultureInfo.InvariantCulture);
        }

        double? min = null, avg = null, max = null, mdev = null;
        if (rtt.Success)
        {
            min = double.Parse(rtt.Groups["min"].Value, CultureInfo.InvariantCulture);
            avg = double.Parse(rtt.Groups["avg"].Value, CultureInfo.InvariantCulture);
            max = double.Parse(rtt.Groups["max"].Value, CultureInfo.InvariantCulture);
            if (rtt.Groups["mdev"].Success)
                mdev = double.Parse(rtt.Groups["mdev"].Value, CultureInfo.InvariantCulture);
        }

        // Fall back to per-reply parsing when summary or rtt line is missing (some busybox
        // builds output one or the other but not both).
        if (received == 0 || avg == null)
        {
            var rtts = ExtractPerReplyRtts(output);
            if (rtts.Count > 0)
            {
                received = Math.Max(received, rtts.Count);
                min ??= rtts.Min();
                max ??= rtts.Max();
                avg ??= rtts.Average();
                if (mdev == null && rtts.Count > 1)
                {
                    var mean = rtts.Average();
                    var variance = rtts.Sum(r => (r - mean) * (r - mean)) / rtts.Count;
                    mdev = Math.Sqrt(variance);
                }
            }
        }

        return new PingProbeResult
        {
            Target = target,
            Vantage = vantage,
            Sent = sent,
            Received = received,
            RttMinMs = min,
            RttAvgMs = avg,
            RttMaxMs = max,
            JitterMs = mdev,
            Timestamp = timestamp ?? DateTime.UtcNow,
            RawOutput = output
        };
    }

    private static List<double> ExtractPerReplyRtts(string output)
    {
        var list = new List<double>();
        foreach (Match m in PerReplyRegex.Matches(output))
        {
            if (double.TryParse(m.Groups["rtt"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                list.Add(v);
        }
        return list;
    }
}
