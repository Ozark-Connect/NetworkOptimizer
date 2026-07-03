namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The HTTPS base URL agents dial back to. It is the app's own reverse-proxied
/// address (a single host serves both the web app and the gRPC tunnel, split by
/// path at the reverse proxy), so it is derived from REVERSE_PROXIED_HOST_NAME -
/// already set on every deployed site - rather than a hand-entered setting.
///
/// Agents require HTTPS, so only the reverse-proxied host qualifies; the
/// plain-HTTP HOST_NAME / HOST_IP fallbacks used elsewhere for internal links
/// are not valid agent endpoints. <see cref="Url"/> is null when no
/// reverse-proxied host is configured (e.g. a bare local run), and the
/// agent-setup UI then tells the operator to set it.
/// </summary>
public class AgentServerUrlProvider
{
    /// <summary>The agent-facing HTTPS base URL, or null when not configured.</summary>
    public string? Url { get; }

    public AgentServerUrlProvider(IConfiguration configuration)
    {
        var host = configuration["REVERSE_PROXIED_HOST_NAME"]?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            Url = null;
            return;
        }
        // REVERSE_PROXIED_HOST_NAME is a bare host (the app prefixes https://
        // elsewhere); tolerate an operator who included the scheme anyway.
        Url = (host.Contains("://") ? host : $"https://{host}").TrimEnd('/');
    }
}
