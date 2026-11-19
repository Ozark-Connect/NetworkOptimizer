using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The link that starts a client WAN speed test from the viewer's browser, shared by the Client WAN
/// Test page and Client Performance's WAN Test button so both start the same test the same way.
/// </summary>
/// <remarks>
/// On an agent-backed site the test starts at the On-Site Agent's /wan/ router, whose interstitial
/// forwards to the external server: the agent becomes the page's referrer, so results post back
/// through it with the real client LAN IP and the site slug stamped on the trusted agent-to-server
/// hop, with no ?site= in any user-facing URL. Direct and default sites link the external server.
/// </remarks>
public static class ClientWanTestLink
{
    /// <summary>
    /// A non-default site with no live agent target. Site attribution for WAN results comes only
    /// from the agent relay, so the bare external URL would record against the default site; a
    /// target not resolved yet (null) counts as offline for the same reason.
    /// </summary>
    public static bool AgentRequiredButOffline(bool isDefaultSite, SiteSpeedTestTargetResolver.Result? target) =>
        !isDefaultSite && target is null or { UsesAgent: false };

    /// <summary>
    /// The site's agent runs on the UniFi gateway itself (monitoring only): no speed-test listener
    /// exists at the site, so there is no relay page to link.
    /// </summary>
    public static bool AgentOnGateway(SiteSpeedTestTargetResolver.Result? target) =>
        target is { AgentOnGateway: true };

    /// <summary>True when a test started now would run and be attributed to this site.</summary>
    public static bool CanRun(bool isDefaultSite, SiteSpeedTestTargetResolver.Result? target, ExternalSpeedTestServer? server) =>
        server != null && !AgentOnGateway(target) && !AgentRequiredButOffline(isDefaultSite, target);

    /// <summary>The test page for <paramref name="server"/> (the default server when null).</summary>
    public static string BaseUrl(SiteSpeedTestTargetResolver.Result? target, ExternalSpeedTestServer? server)
    {
        if (target is { UsesAgent: true, BaseUrl: not null })
        {
            var isDefaultServer = server == null || server.IsDefault;
            return isDefaultServer
                ? $"{target.BaseUrl}/wan/"
                : $"{target.BaseUrl}/wan/{server!.ServerId}/";
        }
        return server?.Url ?? "";
    }

    /// <summary>The same page, starting the test as it opens.</summary>
    public static string RunUrl(SiteSpeedTestTargetResolver.Result? target, ExternalSpeedTestServer? server) =>
        $"{BaseUrl(target, server)}?Run";

    /// <summary>The server a test uses when none is picked: the default, else the first.</summary>
    public static ExternalSpeedTestServer? DefaultServer(IReadOnlyList<ExternalSpeedTestServer> servers) =>
        servers.FirstOrDefault(s => s.IsDefault) ?? servers.FirstOrDefault();
}
