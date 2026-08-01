using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The speed test's resolved identity - host ladder, ports, and the URL clients should open - read
/// from configuration in one place. The CORS origin list in Program.cs, the Client Speed Test page,
/// and the Client Performance page all need the same answers, and each deriving them independently
/// is how they drift apart.
///
/// Host ladder: OPENSPEEDTEST_HOST -> HOST_NAME -> the reverse-proxied host, where the last rung
/// only applies to the new-style config that sets REVERSE_PROXIED_PORT with OPENSPEEDTEST_HTTPS=true
/// (an install serving the app and the speed test on one hostname, separated by port). Gating on
/// REVERSE_PROXIED_PORT keeps every config that predates the variable byte-identical. The rung takes
/// the bare host: any port on that setting is the app's, and OPENSPEEDTEST_HTTPS_PORT supplies the
/// speed test's.
///
/// IMPORTANT: Keep this ladder in sync with docker/openspeedtest/entrypoint.sh (Docker deployment).
/// </summary>
public sealed class OpenSpeedTestSettings
{
    private readonly Lazy<string?> _fallbackIp;

    /// <summary>Resolved speed test hostname, or null when nothing host-like is declared.</summary>
    public string? Host { get; }

    /// <summary>HTTP port the speed test listens on (OPENSPEEDTEST_PORT, default 3005).</summary>
    public string Port { get; }

    /// <summary>Whether the speed test is served over HTTPS through a TLS proxy (OPENSPEEDTEST_HTTPS).</summary>
    public bool HttpsEnabled { get; }

    /// <summary>The proxy's HTTPS port for the speed test (OPENSPEEDTEST_HTTPS_PORT, default 443).</summary>
    public string HttpsPort { get; }

    /// <summary>
    /// HOST_IP, or the auto-detected local IP when unset. Lazy: interface detection only runs if a
    /// caller actually needs it (no <see cref="Host"/> for the URL, or the CORS IP origin).
    /// </summary>
    public string? FallbackIp => _fallbackIp.Value;

    /// <summary>
    /// The URL clients open to run a speed test: HTTPS through the proxy when enabled (443 implicit),
    /// otherwise direct HTTP to <see cref="Host"/> or <see cref="FallbackIp"/>. Null when no host or
    /// IP can be determined.
    /// </summary>
    public string? DisplayUrl
    {
        get
        {
            if (!string.IsNullOrEmpty(Host))
            {
                return HttpsEnabled
                    ? $"https://{NetworkUtilities.ComposeAuthority(Host, HttpsPort, defaultPort: 443)}"
                    : $"http://{Host}:{Port}";
            }
            return string.IsNullOrEmpty(FallbackIp) ? null : $"http://{FallbackIp}:{Port}";
        }
    }

    private OpenSpeedTestSettings(string? host, string port, bool httpsEnabled, string httpsPort, Lazy<string?> fallbackIp)
    {
        Host = host;
        Port = port;
        HttpsEnabled = httpsEnabled;
        HttpsPort = httpsPort;
        _fallbackIp = fallbackIp;
    }

    /// <summary>
    /// Resolve the settings from configuration. <paramref name="detectLocalIp"/> overrides local IP
    /// auto-detection (tests); production callers omit it.
    /// </summary>
    public static OpenSpeedTestSettings Load(IConfiguration configuration, Func<string?>? detectLocalIp = null)
    {
        detectLocalIp ??= NetworkUtilities.DetectLocalIpFromInterfaces;

        // Note: Configuration[] can return empty string (not null) for missing keys, so use IsNullOrEmpty
        var hostName = configuration["HOST_NAME"];
        var hostIp = configuration["HOST_IP"];
        var host = configuration["OPENSPEEDTEST_HOST"];
        var portConfig = configuration["OPENSPEEDTEST_PORT"];
        var port = !string.IsNullOrEmpty(portConfig) ? portConfig : "3005";
        var httpsEnabled = (configuration["OPENSPEEDTEST_HTTPS"] ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
        var httpsPortConfig = configuration["OPENSPEEDTEST_HTTPS_PORT"];
        var httpsPort = !string.IsNullOrEmpty(httpsPortConfig) ? httpsPortConfig : "443";

        if (string.IsNullOrEmpty(host))
            host = hostName;
        if (string.IsNullOrEmpty(host) && httpsEnabled && !string.IsNullOrEmpty(configuration["REVERSE_PROXIED_PORT"]))
            host = NetworkUtilities.AuthorityHost(configuration["REVERSE_PROXIED_HOST_NAME"]);
        if (string.IsNullOrEmpty(host))
            host = null;

        var fallbackIp = new Lazy<string?>(() => !string.IsNullOrEmpty(hostIp) ? hostIp : detectLocalIp());

        return new OpenSpeedTestSettings(host, port, httpsEnabled, httpsPort, fallbackIp);
    }
}
