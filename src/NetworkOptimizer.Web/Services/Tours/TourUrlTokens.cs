using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services.Tours;

/// <summary>
/// Placeholders a step's url may carry, filled in against the site the tour is about to run on.
///
/// A step that wants to land on a particular kind of thing cannot hard-code one: the client, access
/// point or WAN that exists differs per install. Write <c>{name}</c> where landing there is a
/// preference and <c>{!name}</c> where the step is pointless without it.
/// </summary>
public static class TourUrlTokens
{
    /// <summary>An online wireless client's address.</summary>
    public const string WifiClientIp = "wifi-client-ip";

    private static readonly string[] Names = [WifiClientIp];

    /// <summary>One placeholder as it appears in a url.</summary>
    /// <param name="Placeholder">The literal text to substitute, braces included.</param>
    /// <param name="Name">Which token it is.</param>
    /// <param name="Required">True when an unresolved value should drop the step.</param>
    public readonly record struct Use(string Placeholder, string Name, bool Required);

    /// <summary>Every placeholder this url carries.</summary>
    public static IEnumerable<Use> Used(string url)
    {
        foreach (var name in Names)
        {
            var optional = "{" + name + "}";
            if (url.Contains(optional, StringComparison.Ordinal))
                yield return new Use(optional, name, false);

            var required = "{!" + name + "}";
            if (url.Contains(required, StringComparison.Ordinal))
                yield return new Use(required, name, true);
        }
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
        var needed = urls.SelectMany(TourUrlTokens.Used)
            .Select(u => u.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in needed)
        {
            values[name] = name switch
            {
                TourUrlTokens.WifiClientIp => await WifiClientIpAsync(siteSlug),
                _ => null,
            };
        }
        return values;
    }

    /// <summary>
    /// Substitutes resolved tokens. An unresolved optional token takes its query parameter with it,
    /// leaving a url that still works; an unresolved required one returns null, and the caller drops
    /// the step rather than send a viewer somewhere the step is not about.
    /// </summary>
    public static string? Fill(string url, IReadOnlyDictionary<string, string?> values)
    {
        foreach (var use in TourUrlTokens.Used(url))
        {
            values.TryGetValue(use.Name, out var value);
            if (!string.IsNullOrEmpty(value))
                url = url.Replace(use.Placeholder, Uri.EscapeDataString(value), StringComparison.Ordinal);
            else if (use.Required)
                return null;
            else
                url = DropParameter(url, use.Placeholder);
        }
        return url;
    }

    private static string DropParameter(string url, string placeholder)
    {
        var cleaned = Regex.Replace(url, @"[?&][^?&=]*=" + Regex.Escape(placeholder), string.Empty);

        // The dropped parameter may have been the one carrying the '?'.
        if (!cleaned.Contains('?') && cleaned.Contains('&'))
        {
            var at = cleaned.IndexOf('&');
            cleaned = string.Concat(cleaned.AsSpan(0, at), "?", cleaned.AsSpan(at + 1));
        }
        return cleaned;
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
