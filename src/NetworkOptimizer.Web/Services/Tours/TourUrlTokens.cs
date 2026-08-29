using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;

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
    /// <summary>
    /// An online wireless client's address: the one with the most LAN speed test results, else a
    /// phone, else whichever comes first.
    /// </summary>
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
    private readonly SiteContextService _siteContext;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly FingerprintDatabaseService _fingerprints;
    private readonly IeeeOuiDatabase _oui;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TourUrlTokenResolver> _logger;

    /// <summary>Creates the resolver.</summary>
    public TourUrlTokenResolver(
        SiteConnectionRegistry connections,
        SiteContextService siteContext,
        SiteDbContextFactory siteDbFactory,
        FingerprintDatabaseService fingerprints,
        IeeeOuiDatabase oui,
        ILoggerFactory loggerFactory)
    {
        _connections = connections;
        _siteContext = siteContext;
        _siteDbFactory = siteDbFactory;
        _fingerprints = fingerprints;
        _oui = oui;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TourUrlTokenResolver>();
    }

    /// <summary>A wireless client in the running for <see cref="TourUrlTokens.WifiClientIp"/>.</summary>
    /// <param name="Ip">The address the step would land on.</param>
    /// <param name="LanTests">LAN speed test results recorded against the client's MAC.</param>
    /// <param name="NamedPhone">The name or hostname says "phone".</param>
    /// <param name="DetectedPhone">Device detection (fingerprint, vendor, name) calls it a phone.</param>
    public readonly record struct WifiClientCandidate(string Ip, int LanTests, bool NamedPhone, bool DetectedPhone);

    /// <summary>
    /// The client a Client Performance step should open on. Speed test history first, since the page
    /// has the most to show there; then a phone, the device a walk test is done from; then anything.
    /// </summary>
    public static string? PickWifiClient(IReadOnlyList<WifiClientCandidate> candidates)
    {
        if (candidates.Count == 0)
            return null;
        var tested = candidates.Where(c => c.LanTests > 0).OrderByDescending(c => c.LanTests).ToList();
        if (tested.Count > 0)
            return tested[0].Ip;
        foreach (var c in candidates)
            if (c.NamedPhone)
                return c.Ip;
        foreach (var c in candidates)
            if (c.DetectedPhone)
                return c.Ip;
        return candidates[0].Ip;
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

            var clients = (await connection.Client.GetClientsAsync() ?? new List<UniFiClientResponse>())
                .Where(c => !c.IsWired && !string.IsNullOrEmpty(c.BestIp))
                .ToList();
            if (clients.Count == 0)
                return null;

            var tests = await LanTestCountsAsync(siteSlug);
            var detection = new DeviceTypeDetectionService(
                _loggerFactory.CreateLogger<DeviceTypeDetectionService>(),
                await _fingerprints.GetDatabaseAsync(),
                _oui,
                _loggerFactory);

            var candidates = clients.Select(c => new WifiClientCandidate(
                c.BestIp!,
                tests.GetValueOrDefault(c.Mac.ToLowerInvariant()),
                SaysPhone(c.Name) || SaysPhone(c.Hostname),
                detection.DetectDeviceType(c).Category == ClientDeviceCategory.Smartphone)).ToList();
            return PickWifiClient(candidates);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tour could not resolve a Wi-Fi client for site {Site}", siteSlug);
            return null;
        }
    }

    private static bool SaysPhone(string? name) =>
        !string.IsNullOrEmpty(name) && name.Contains("phone", StringComparison.OrdinalIgnoreCase);

    /// <summary>LAN speed test results per client MAC (lower-case), the same directions Client Performance shows.</summary>
    private async Task<Dictionary<string, int>> LanTestCountsAsync(string siteSlug)
    {
        var isDefault = siteSlug == _siteContext.Slug && _siteContext.IsDefault;
        await using var db = _siteDbFactory.CreateForSite(siteSlug, isDefault);
        var rows = await db.Iperf3Results
            .Where(r => r.ClientMac != null
                && (r.Direction == SpeedTestDirection.ServerToDevice
                    || r.Direction == SpeedTestDirection.ClientToServer
                    || r.Direction == SpeedTestDirection.BrowserToServer))
            .GroupBy(r => r.ClientMac!)
            .Select(g => new { Mac = g.Key, Count = g.Count() })
            .ToListAsync();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
            counts[row.Mac.ToLowerInvariant()] = counts.GetValueOrDefault(row.Mac.ToLowerInvariant()) + row.Count;
        return counts;
    }
}
