namespace NetworkOptimizer.Web.Services.Tours;

/// <summary>
/// Placeholders a step's url may carry, filled in against the site the tour is about to run on.
///
/// A step that must land on a particular kind of thing cannot hard-code one: the client, access
/// point or WAN that exists differs per install. A step whose token does not resolve is dropped
/// rather than navigated to, because the literal placeholder in a query string lands the viewer on
/// a page about nothing.
/// </summary>
public static class TourUrlTokens
{
    /// <summary>An online wireless client's address, for a step whose target only exists on one.</summary>
    public const string WifiClientIp = "{wifi-client-ip}";

    /// <summary>Every token this step's url depends on.</summary>
    public static IEnumerable<string> Used(string url)
    {
        if (url.Contains(WifiClientIp, StringComparison.Ordinal))
            yield return WifiClientIp;
    }
}

/// <summary>
/// Resolves <see cref="TourUrlTokens"/> against a site. Nothing is looked up unless a step actually
/// carries the token, so the common tour costs no console calls.
/// </summary>
public sealed class TourUrlTokenResolver
{
    private readonly SiteConnectionRegistry _connections;
    private readonly ILogger<TourUrlTokenResolver> _logger;

    /// <summary>Creates the resolver.</summary>
    public TourUrlTokenResolver(SiteConnectionRegistry connections, ILogger<TourUrlTokenResolver> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    /// <summary>
    /// Values for every token the given urls use, resolved once per pass. A token maps to null when
    /// the site has nothing to point it at.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> ResolveAsync(IEnumerable<string> urls, string siteSlug)
    {
        var needed = urls.SelectMany(TourUrlTokens.Used).Distinct(StringComparer.Ordinal).ToList();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var token in needed)
        {
            values[token] = token switch
            {
                TourUrlTokens.WifiClientIp => await WifiClientIpAsync(siteSlug),
                _ => null,
            };
        }

        return values;
    }

    /// <summary>Substitutes resolved tokens; null when one this url needs did not resolve.</summary>
    public static string? Fill(string url, IReadOnlyDictionary<string, string?> values)
    {
        foreach (var token in TourUrlTokens.Used(url))
        {
            if (!values.TryGetValue(token, out var value) || string.IsNullOrEmpty(value))
                return null;
            url = url.Replace(token, Uri.EscapeDataString(value), StringComparison.Ordinal);
        }
        return url;
    }

    private async Task<string?> WifiClientIpAsync(string siteSlug)
    {
        try
        {
            var connection = _connections.GetFor(siteSlug);
            if (!connection.IsConnected || connection.Client == null)
                return null;

            var clients = await connection.Client.GetClientsAsync();
            var pick = (clients ?? new List<NetworkOptimizer.UniFi.Models.UniFiClientResponse>())
                .FirstOrDefault(c => !c.IsWired && !string.IsNullOrEmpty(c.BestIp));
            return pick?.BestIp;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tour could not resolve a Wi-Fi client for site {Site}", siteSlug);
            return null;
        }
    }
}
