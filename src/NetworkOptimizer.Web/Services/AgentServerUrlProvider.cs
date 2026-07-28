namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The HTTPS base URL agents dial back to. It is the app's own reverse-proxied address (a single host
/// serves both the web app and the gRPC tunnel, split by path at the reverse proxy).
///
/// Agents require HTTPS, so this takes the reverse-proxied rung of
/// <see cref="CanonicalBaseUrlProvider"/> and stops there: the plain-HTTP HOST_NAME / HOST_IP rungs
/// used elsewhere are not valid agent endpoints. <see cref="Url"/> is null when no reverse-proxied host
/// is configured (e.g. a bare local run), and the agent-setup UI then tells the operator to set it.
/// </summary>
public class AgentServerUrlProvider
{
    /// <summary>The agent-facing HTTPS base URL, or null when not configured.</summary>
    public string? Url { get; }

    public AgentServerUrlProvider(CanonicalBaseUrlProvider canonical) => Url = canonical.HttpsUrl;
}
