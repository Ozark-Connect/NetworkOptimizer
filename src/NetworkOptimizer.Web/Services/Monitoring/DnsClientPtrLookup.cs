using System.Net;
using DnsClient;
using DnsClient.Protocol;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// A PTR-only reverse lookup for <see cref="NetworkOptimizer.Core.Helpers.ReverseDnsCache"/>: one PTR
/// query to the system resolvers, with no forward lookup of the answer and so no search-domain retries.
/// </summary>
public static class DnsClientPtrLookup
{
    private static readonly LookupClient Client = new(new LookupClientOptions
    {
        UseCache = false, // ReverseDnsCache caches, hits and misses alike
        Timeout = TimeSpan.FromSeconds(2),
        Retries = 0,
    });

    /// <summary>The PTR name for an address, or null when it has none.</summary>
    public static async Task<string?> LookupAsync(string ip, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var address)) return null;
        var response = await Client.QueryReverseAsync(address, ct);
        return response.Answers.OfType<PtrRecord>().FirstOrDefault()?.PtrDomainName.Value;
    }
}
