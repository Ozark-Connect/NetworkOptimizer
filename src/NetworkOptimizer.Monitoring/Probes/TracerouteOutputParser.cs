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

    private static readonly Regex RttRegex = new(
        @"(\d+(?:\.\d+)?)\s*ms",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static TracerouteResult Parse(
        string output,
        ProbeTarget target,
        ProbeVantage vantage,
        ProbeMode modeUsed,
        DateTime? timestamp = null)
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
                Responses = responses
            });

            if (!string.IsNullOrEmpty(address) && string.Equals(address, targetAddress, StringComparison.OrdinalIgnoreCase))
                reached = true;
        }

        return new TracerouteResult
        {
            Target = target,
            Vantage = vantage,
            ModeUsed = modeUsed,
            Hops = hops,
            Reached = reached,
            RawOutput = output,
            Timestamp = timestamp ?? DateTime.UtcNow
        };
    }

    private static int CountStars(string rest)
    {
        int count = 0;
        foreach (var ch in rest)
            if (ch == '*') count++;
        return count == 0 ? 3 : count;
    }
}
