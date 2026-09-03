using System.Net;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Whether an operator- or console-supplied address is safe to embed somewhere it is not
/// re-parsed: a quoted shell argument, a JavaScript string literal, an attribute. The checks are
/// about shape, not reachability.
/// </summary>
public static partial class UrlSafety
{
    /// <summary>
    /// An absolute http or https URL with nothing in it that could end a surrounding quote:
    /// no whitespace, control characters, or single or double quotes.
    /// </summary>
    public static bool IsSafeHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        return !url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\'' || c == '"');
    }

    /// <summary>
    /// A bare host for a URL to be built around - hostname, IPv4, or bracketed IPv6 - with an
    /// optional port, and nothing else.
    /// </summary>
    public static bool IsSafeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (IPAddress.TryParse(host, out _)) return true;
        return HostWithOptionalPort().IsMatch(host);
    }

    /// <summary>Either shape an operator may type as a speed-test target: a full URL or a bare host.</summary>
    public static bool IsSafeHostOrHttpUrl(string? value) =>
        IsSafeHttpUrl(value) || IsSafeHost(value);

    [GeneratedRegex(@"^(?:[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?)*|\[[0-9A-Fa-f:.]+\])(?::\d{1,5})?$")]
    private static partial Regex HostWithOptionalPort();
}
