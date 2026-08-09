using System.Text.RegularExpressions;

namespace NetworkOptimizer.Monitoring.Probes;

/// <summary>
/// Parses nslookup output into a <see cref="DnsLookupResult"/>. Tolerant of partial or unusual
/// output - never throws, and returns null fields rather than fabricating.
///
/// Four dialects were captured across a UniFi estate and the parser is built on what separates
/// them, not on any one format:
/// <list type="bullet">
/// <item>BIND (gateway): <c>Server:</c>/<c>Address: x#53</c> header, unnumbered <c>Address:</c> answers.</item>
/// <item>busybox on APs: same shape but <c>x:53</c>, and A and AAAA arrive in two separate blocks.</item>
/// <item>busybox on switches and bridges: no server line at all, numbered <c>Address 1:</c> answers.</item>
/// <item>busybox on XG switches: server line present, numbered answers, and NXDOMAIN exits 0.</item>
/// </list>
/// Hence three rules that are easy to get wrong: the header's <c>Address:</c> is the resolver and
/// must never be collected as an answer; <c>can't resolve '(null)'</c> is printed on SUCCESSFUL
/// lookups by several builds and is not a failure; and exit codes cannot be trusted, so
/// not-found is decided from the text.
/// </summary>
public static class NslookupOutputParser
{
    /// <summary>Identifies this result as a real lookup. See <see cref="DnsLookupResult.Kind"/>.</summary>
    public const string ResultKind = "dns";

    // "Server:\t192.168.99.1" - the resolver, on the builds that name one.
    private static readonly Regex ServerRegex = new(
        @"^\s*Server:\s*(?<server>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // Answers, numbered or not: "Address 1: 1.2.3.4" / "Address: 1.2.3.4".
    // A trailing name appears on busybox reverse lookups: "Address 1: 1.1.1.1 one.one.one.one".
    private static readonly Regex AddressRegex = new(
        @"^\s*Address\s*\d*\s*:\s*(?<addr>[0-9A-Fa-f:.]+)(?:\s+(?<name>\S+))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // BIND-style reverse answer: "1.1.1.1.in-addr.arpa\tname = one.one.one.one."
    private static readonly Regex PtrRegex = new(
        @"name\s*=\s*(?<name>[^\s]+?)\.?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // Only meaningful after StripKnownNoise: the (null) form of "can't resolve" is printed on
    // successful lookups too, so it must be removed before any of this counts as not-found.
    private static readonly Regex NotFoundRegex = new(
        @"NXDOMAIN|can't\s+find|can't\s+resolve|does\s+not\s+resolve|No\s+answer|Name\s+or\s+service\s+not\s+known",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse nslookup output. <paramref name="reverse"/> tells the parser the query was a PTR
    /// lookup, where a returned address is the query echoed back rather than an answer.
    /// </summary>
    public static DnsLookupResult Parse(
        string output,
        ProbeTarget target,
        ProbeVantage vantage,
        bool reverse = false,
        DateTime? timestamp = null)
    {
        var result = new DnsLookupResult
        {
            Kind = ResultKind,
            Target = target,
            Vantage = vantage,
            Timestamp = timestamp ?? DateTime.UtcNow,
            RawOutput = output
        };

        if (string.IsNullOrWhiteSpace(output))
            return result with { ErrorMessage = "No output from nslookup" };

        var server = ServerRegex.Match(output);
        var resolver = server.Success ? StripPort(server.Groups["server"].Value) : null;

        var addresses = new List<string>();
        string? ptrName = null;

        // An Address line is an answer only once a Name line has introduced one. The header
        // block that restates the resolver never has one, and its address carries a port, so
        // matching the resolver by value alone misses it and reports your own DNS server as a
        // result. True of every dialect surveyed.
        var inAnswers = false;
        foreach (var line in output.Split('\n'))
        {
            if (line.TrimStart().StartsWith("Name", StringComparison.OrdinalIgnoreCase))
            {
                inAnswers = true;
                continue;
            }

            var m = AddressRegex.Match(line);
            if (!m.Success || !inAnswers) continue;

            // busybox reverse lookups hang the PTR name off the address line.
            if (m.Groups["name"].Success) ptrName ??= m.Groups["name"].Value.TrimEnd('.');

            var address = StripPort(m.Groups["addr"].Value);
            if (!addresses.Contains(address, StringComparer.OrdinalIgnoreCase))
                addresses.Add(address);
        }

        if (ptrName == null && PtrRegex.Match(output) is { Success: true } ptr)
            ptrName = ptr.Groups["name"].Value.TrimEnd('.');

        // On a reverse lookup the address is the question, not the answer.
        if (reverse) addresses.Clear();

        var notFound = addresses.Count == 0
                       && string.IsNullOrEmpty(ptrName)
                       && NotFoundRegex.IsMatch(StripKnownNoise(output));

        return result with
        {
            Resolver = resolver,
            Addresses = addresses,
            CanonicalName = ptrName,
            NotFound = notFound
        };
    }

    /// <summary>
    /// Drops the line several busybox builds print on every query, including successful ones.
    /// Left in, it reads as a not-found on three of the five device classes surveyed.
    /// </summary>
    private static string StripKnownNoise(string output) =>
        output.Replace("can't resolve '(null)'", string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>Server lines carry a port as <c>x#53</c> (BIND) or <c>x:53</c> (busybox).</summary>
    private static string StripPort(string server)
    {
        var hash = server.IndexOf('#');
        if (hash > 0) return server[..hash];

        // Only strip a colon port from IPv4; an IPv6 literal is all colons.
        var colon = server.LastIndexOf(':');
        if (colon > 0 && server.IndexOf(':') == colon) return server[..colon];

        return server;
    }
}
