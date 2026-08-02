using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Resolves the site-local speed-test target for the current site - the single
/// source of truth the Client Dashboard, Client (LAN) Speed Test page, and the
/// WAN speed test link all share. On an agent-backed site the client-facing
/// target is the on-site agent (per-site URL override wins over the online
/// agent's reported LAN IP); on the default/direct site there is no agent
/// target and callers fall through to their central-server configuration.
/// </summary>
public class SiteSpeedTestTargetResolver
{
    /// <summary>The agent's nginx speed-test listener (self-signed https).</summary>
    /// <summary>
    /// Where an agent serves its LAN speed test page when it does not say otherwise. Agents announce
    /// their port in the tunnel hello now; this is what agents predating that announcement serve,
    /// and it is what the server assumed unconditionally before.
    /// </summary>
    public const int AgentOpenSpeedTestPort = 3000;

    /// <summary>
    /// The resolved site-local target.
    /// </summary>
    /// <param name="EffectiveTarget">The override or agent LAN IP, or null when the site has neither.</param>
    /// <param name="BaseUrl">Scheme-prefixed base URL of the agent's speed-test listener (no trailing slash), or null.</param>
    /// <param name="Host">Bare host of the target (for display), or null.</param>
    /// <param name="UsesAgent">True when clients should be pointed at the site-local agent.</param>
    /// <param name="AgentOffline">True when the site reported no online agent LAN IP (an override may still apply).</param>
    /// <param name="AgentOnGateway">True when the site's agent runs on the UniFi gateway itself, which hosts no
    /// speed-test listener - there is no client-facing target, and pages explain why instead of showing one.</param>
    public sealed record Result(
        string? EffectiveTarget,
        string? BaseUrl,
        string? Host,
        bool UsesAgent,
        bool AgentOffline,
        bool AgentOnGateway = false);

    private readonly SiteContextService _siteContext;
    private readonly AgentEnrollmentService _agentEnrollment;
    private readonly ISystemSettingsService _settings;
    private readonly AgentOnGatewayDetector _onGatewayDetector;
    private readonly AgentTunnelRegistry _tunnelRegistry;
    private readonly SiteAgentCoverage _agentCoverage;

    public SiteSpeedTestTargetResolver(
        SiteContextService siteContext,
        AgentEnrollmentService agentEnrollment,
        ISystemSettingsService settings,
        AgentOnGatewayDetector onGatewayDetector,
        SiteAgentCoverage agentCoverage,
        AgentTunnelRegistry tunnelRegistry)
    {
        _siteContext = siteContext;
        _agentEnrollment = agentEnrollment;
        _settings = settings;
        _onGatewayDetector = onGatewayDetector;
        _agentCoverage = agentCoverage;
        _tunnelRegistry = tunnelRegistry;
    }

    /// <summary>
    /// The port the site's connected agent says it serves its speed test page on, or the historic
    /// 3000 when it does not say. Several agents on one site are possible, so the first that
    /// announces a port wins - they serve the same page and a site with two disagreeing agents has
    /// a bigger problem than which one a link points at.
    /// </summary>
    private int AgentSpeedTestPortFor(string slug)
    {
        var announced = _tunnelRegistry.GetForSite(slug).FirstOrDefault(c => c.SpeedTestPort > 0);
        return announced?.SpeedTestPort ?? AgentOpenSpeedTestPort;
    }

    /// <summary>
    /// Resolves the current site's client-facing speed-test target. A per-site
    /// override (an IP/host or a full URL) wins over the auto-detected agent LAN
    /// IP - for agents whose reachable address isn't their detected LAN IP (e.g.
    /// behind a reverse proxy). An override that is a full URL is used as-is, so
    /// an operator can force http:// or a different host/port; a bare host/IP
    /// defaults to https on the agent port.
    /// </summary>
    public async Task<Result> ResolveAsync()
    {
        // The main site normally has no agent to point clients at - this server IS the site's
        // speed test host. Once its agent covers the site, it is the host, exactly as on a
        // secondary site.
        if (_siteContext.IsDefault && !await _agentCoverage.CoversAsync(_siteContext.Slug))
            return new Result(null, null, null, UsesAgent: false, AgentOffline: false);

        var targetOverride = (await _settings.GetAsync(SystemSettingKeys.ClientSpeedTestTargetOverride))?.Trim();
        var agentLanIp = await _agentEnrollment.GetOnlineAgentLanIpAsync(_siteContext.Slug);
        var agentOffline = agentLanIp == null;

        // An agent on the gateway itself reports the gateway's IP (usually the WAN
        // address) and hosts no speed-test listener, so there is nothing to point
        // clients at - the "target" would be the router. An explicit override still
        // wins (a separate box the operator knows about). When speed-test-capable
        // gateway installs arrive, replace this location check with the agent's
        // speed-test capability so the config can flow through.
        if (string.IsNullOrEmpty(targetOverride) && agentLanIp != null
            && await _onGatewayDetector.IsAgentOnGatewayAsync(_siteContext.Slug))
        {
            return new Result(null, null, null, UsesAgent: false, AgentOffline: false, AgentOnGateway: true);
        }

        var effectiveTarget = !string.IsNullOrEmpty(targetOverride) ? targetOverride : agentLanIp;
        if (string.IsNullOrEmpty(effectiveTarget))
            return new Result(null, null, null, UsesAgent: false, AgentOffline: agentOffline);

        string baseUrl, host;
        if (effectiveTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || effectiveTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = effectiveTarget.TrimEnd('/');
            host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : effectiveTarget;
        }
        else
        {
            baseUrl = $"https://{effectiveTarget}:{AgentSpeedTestPortFor(_siteContext.Slug)}";
            host = effectiveTarget;
        }

        return new Result(effectiveTarget, baseUrl, host, UsesAgent: true, AgentOffline: agentOffline);
    }
}
