using System.Collections.Concurrent;
using System.Net;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Address plus PTR name for display, cached hard because it sits on a render path.
///
/// ISP Health recomputes often and shows every access hop at once, so an uncached lookup would put a
/// burst of DNS on every load and stall the panel behind whichever hop has no PTR. Results are cached
/// for hours and FAILURES are cached too: a router with no PTR is the common case, and retrying it on
/// every render is how you turn a missing record into a permanent delay.
///
/// Deliberately separate from <c>DohProviderRegistry.ReverseDnsLookupAsync</c>, which is an uncached
/// wrapper wired into DoH provider identification. Same system call, different job: that one runs once
/// during an audit and wants the raw answer, this one runs constantly and must never cost anything
/// twice.
/// </summary>
public static class ReverseDnsCache
{
    private static readonly TimeSpan FoundTtl = TimeSpan.FromHours(6);

    /// <summary>Shorter than a hit: a hop with no PTR today may be given one, and the retry is cheap once a day.</summary>
    private static readonly TimeSpan MissTtl = TimeSpan.FromHours(1);

    /// <summary>A hop that never answers must not hold a render open.</summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, (string Ip, string? Hostname, DateTime Expires)> Cache = new();

    /// <summary>
    /// A PTR-only lookup the host can supply (the server sets one built on DnsClient at startup).
    /// Without it the lookup falls back to <c>Dns.GetHostEntryAsync</c>, which forward-resolves the
    /// PTR name as well; a name that does not resolve forward is then retried with every search
    /// domain in resolv.conf appended, which floods the site's resolver with junk queries.
    /// </summary>
    public static Func<string, CancellationToken, Task<string?>>? PtrLookup { get; set; }

    /// <summary>
    /// Resolves an address as configured (an IP or a hostname) to the pair worth showing: the literal
    /// address it answers on, and its PTR name when it has one.
    ///
    /// A hostname target is forward-resolved first, so the IP is always the one actually reached rather
    /// than the label somebody typed - which is the point of showing it beside a friendly hop name.
    /// </summary>
    public static async Task<(string Ip, string? Hostname)> ResolveAsync(string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return (address, null);

        if (Cache.TryGetValue(address, out var hit) && hit.Expires > DateTime.UtcNow)
            return (hit.Ip, hit.Hostname);

        var ip = address;
        string? hostname = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LookupTimeout);

            if (!IPAddress.TryParse(address, out var parsed))
            {
                var forward = await Dns.GetHostAddressesAsync(address, timeout.Token);
                parsed = forward.FirstOrDefault();
                if (parsed is not null)
                    ip = parsed.ToString();
            }

            if (parsed is not null)
            {
                string? name;
                if (PtrLookup is { } ptr)
                {
                    name = await ptr(ip, timeout.Token);
                }
                else
                {
                    // By string, not IPAddress: only the string overload takes a token, and an IP
                    // literal here is still a PTR lookup.
                    name = (await Dns.GetHostEntryAsync(ip, timeout.Token)).HostName;
                }
                // A resolver that echoes the address back has told us nothing worth showing.
                if (!string.IsNullOrWhiteSpace(name)
                    && !string.Equals(name, ip, StringComparison.OrdinalIgnoreCase))
                {
                    hostname = name.TrimEnd('.');
                }
            }
        }
        catch
        {
            // No PTR, no answer, or too slow. All three mean "show the address alone", and all three
            // are cached so the next render does not pay for them again.
        }

        Cache[address] = (ip, hostname, DateTime.UtcNow.Add(hostname is null ? MissTtl : FoundTtl));
        return (ip, hostname);
    }

    /// <summary>The display form: the address, and the PTR name after it when there is one.</summary>
    public static string Format(string ip, string? hostname)
        => string.IsNullOrWhiteSpace(hostname) ? ip : $"{ip} - {hostname}";
}
