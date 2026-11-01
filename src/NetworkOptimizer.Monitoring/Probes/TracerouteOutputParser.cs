using System.Globalization;
using System.Text.RegularExpressions;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Monitoring.Probes;

/// <summary>
/// Parses traceroute output from both standard Linux (iputils/traceroute package) and
/// BusyBox. Tolerant of partial output — non-responding hops surface as TraceHop entries
/// with Address=null instead of being dropped.
/// </summary>
public static class TracerouteOutputParser
{
    // Examples this needs to handle:
    //   " 1  10.0.0.1 (10.0.0.1)  1.234 ms  2.345 ms  3.456 ms"
    //   " 1  10.0.0.1  1.234 ms"
    //   " 2  *  *  *"
    //   " 3  router.example.com (203.0.113.1)  5.6 ms  6.7 ms !H"
    //   "10  * * *"
    private static readonly Regex HopLineRegex = new(
        @"^\s*(?<hop>\d+)\s+(?<rest>.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex IpAddressRegex = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled);

    // Hostname here is "whatever the resolver returned before the (ip)". Be permissive:
    // Linux can label the gateway as `_gateway`; DHCP-issued PTRs sometimes start with a
    // digit; vendor PTRs contain hyphens and dots. We anchor only on the trailing `(ip)`.
    private static readonly Regex HostnameRegex = new(
        @"(?<host>[A-Za-z0-9_][A-Za-z0-9\-\._]{0,253})\s*\((?<ip>(?:\d{1,3}\.){3}\d{1,3})\)",
        RegexOptions.Compiled);

    // "traceroute to host.example (203.0.113.10), 30 hops max": what a hostname resolved to.
    private static readonly Regex HeaderRegex = new(
        @"^traceroute to \S+ \((?<ip>(?:\d{1,3}\.){3}\d{1,3})\)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Linux traceroute --back: a probe whose reply came home by a different number of hops than
    // it went out prints that count after its RTT, e.g. "12.3 ms '-9'".
    private static readonly Regex BackHopsRegex = new(
        @"'-(\d+)'",
        RegexOptions.Compiled);

    private static readonly Regex RttRegex = new(
        @"(\d+(?:\.\d+)?)\s*ms",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static TracerouteResult Parse(
        string output,
        ProbeTarget target,
        ProbeVantage vantage,
        ProbeMode modeUsed,
        DateTime? timestamp = null,
        bool backHopsReported = false)
    {
        var hops = new List<TraceHop>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return new TracerouteResult
            {
                Target = target,
                Vantage = vantage,
                ModeUsed = modeUsed,
                Hops = hops,
                Reached = false,
                ErrorMessage = "No output from traceroute",
                RawOutput = output,
                Timestamp = timestamp ?? DateTime.UtcNow
            };
        }

        bool reached = false;
        var targetAddress = target.Address;
        var header = HeaderRegex.Match(output);
        var resolved = header.Success && !string.Equals(header.Groups["ip"].Value, targetAddress, StringComparison.OrdinalIgnoreCase)
            ? header.Groups["ip"].Value
            : null;

        foreach (Match m in HopLineRegex.Matches(output))
        {
            if (!int.TryParse(m.Groups["hop"].Value, out var hopNum))
                continue;
            var rest = m.Groups["rest"].Value;

            string? hostname = null;
            string? address = null;

            var hostMatch = HostnameRegex.Match(rest);
            if (hostMatch.Success)
            {
                hostname = hostMatch.Groups["host"].Value;
                address = hostMatch.Groups["ip"].Value;
            }
            else
            {
                var ipMatch = IpAddressRegex.Match(rest);
                if (ipMatch.Success)
                {
                    address = ipMatch.Value;
                }
            }

            var rttValues = new List<double>();
            foreach (Match rm in RttRegex.Matches(rest))
            {
                if (double.TryParse(rm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    rttValues.Add(v);
            }

            int stars = CountStars(rest);
            int responses = rttValues.Count;
            int probes = stars + responses;
            if (probes == 0) probes = 1;

            // Fully non-responding hop (all probes timed out)
            if (responses == 0)
            {
                hops.Add(new TraceHop
                {
                    HopNumber = hopNum,
                    Probes = probes,
                    Responses = 0
                });
                continue;
            }

            hops.Add(new TraceHop
            {
                HopNumber = hopNum,
                Address = address,
                Hostname = hostname,
                RttMinMs = rttValues.Count > 0 ? rttValues.Min() : null,
                RttAvgMs = rttValues.Count > 0 ? rttValues.Average() : null,
                RttMaxMs = rttValues.Count > 0 ? rttValues.Max() : null,
                Probes = probes,
                Responses = responses,
                ReturnHops = ParseReturnHops(rest, hopNum, responses, backHopsReported)
            });

            if (!string.IsNullOrEmpty(address)
                && (string.Equals(address, targetAddress, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(address, resolved, StringComparison.OrdinalIgnoreCase)))
                reached = true;
        }

        return new TracerouteResult
        {
            Target = target,
            Vantage = vantage,
            ModeUsed = modeUsed,
            Hops = hops,
            ResolvedAddress = resolved,
            Reached = reached,
            RawOutput = output,
            Timestamp = timestamp ?? DateTime.UtcNow
        };
    }

    /// <summary>
    /// The return hop count <c>--back</c> reported for a hop: the most common one across its replies.
    /// The binary only prints a count that differs from the forward one, so every reply that printed
    /// none came home by <paramref name="hopNum"/> hops and votes for that - one odd reply among five
    /// symmetric ones must not make the hop read as asymmetric. Null when the trace ran without
    /// <c>--back</c>, where silence means nothing.
    /// </summary>
    private static int? ParseReturnHops(string rest, int hopNum, int responses, bool backHopsReported)
    {
        if (!backHopsReported || responses == 0) return null;
        var printed = BackHopsRegex.Matches(rest)
            .Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : -1)
            .Where(n => n > 0)
            .ToList();
        var votes = printed.Concat(Enumerable.Repeat(hopNum, Math.Max(0, responses - printed.Count)));
        return votes.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => Math.Abs(g.Key - hopNum)).First().Key;
    }

    private static int CountStars(string rest)
    {
        int count = 0;
        foreach (var ch in rest)
            if (ch == '*') count++;
        return count;
    }
}
