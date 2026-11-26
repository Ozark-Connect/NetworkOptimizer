namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The link to a Client's Client Performance page. It names the Client by MAC where one is known, so an old
/// result or an asleep Client opens as itself after its address has moved on to another device, and carries
/// the IP as well: the page opens by that only when UniFi Network has never heard of the MAC.
/// </summary>
public static class ClientPerformanceLink
{
    /// <summary>
    /// The page's URL, or null with neither a MAC nor an IP. <paramref name="query"/> is appended as given
    /// ("tab=speed&amp;range=30d"). The URL is not HTML-encoded: encode it when writing it into raw HTML.
    /// </summary>
    public static string? Href(string? mac, string? ip, string? query = null)
    {
        var parts = new List<string>(3);
        if (CableModemProviders.Uci.UciInformService.NormalizeMac(mac) is { } normalized)
            parts.Add("mac=" + Uri.EscapeDataString(normalized));
        if (!string.IsNullOrWhiteSpace(ip))
            parts.Add("ip=" + Uri.EscapeDataString(ip.Trim()));
        if (parts.Count == 0) return null;
        if (!string.IsNullOrEmpty(query)) parts.Add(query);
        return "/client-dashboard?" + string.Join("&", parts);
    }
}
