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
    private readonly SiteSpeedTestHostSelector _hostSelector;
    private readonly ISystemSettingsService _settings;
    private readonly AgentTunnelRegistry _tunnelRegistry;
    private readonly SiteAgentCoverage _agentCoverage;

    public SiteSpeedTestTargetResolver(
        SiteContextService siteContext,
        SiteSpeedTestHostSelector hostSelector,
        ISystemSettingsService settings,
        SiteAgentCoverage agentCoverage,
        AgentTunnelRegistry tunnelRegistry)
    {
        _siteContext = siteContext;
        _hostSelector = hostSelector;
        _settings = settings;
        _agentCoverage = agentCoverage;
        _tunnelRegistry = tunnelRegistry;
    }

    /// <summary>
    /// Splits a bare target into the host as it goes into a URL and the port it carried, if any:
    /// "host:3000" and "[2001:db8::1]:3000" yield the port; a bare IPv6 literal comes back
    /// bracketed with no port; anything else is returned as it was.
    /// </summary>
    internal static (string Host, int? Port) SplitHostAndPort(string value)
    {
        if (System.Net.IPAddress.TryParse(value, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ($"[{value}]", null);
        }

        var match = System.Text.RegularExpressions.Regex.Match(value, @"^(?<host>\[[^\]]+\]|[^:]+):(?<port>\d{1,5})$");
        return match.Success && int.TryParse(match.Groups["port"].Value, out var port)
            ? (match.Groups["host"].Value, port)
            : (value, null);
    }

    /// <summary>
    /// The port a bare-host override is served on: the selected agent's, else the first any
    /// connected agent announces, else the historic 3000.
    /// </summary>
    private int AgentSpeedTestPortFor(string slug, SiteSpeedTestHostSelector.Selection selection)
    {
        if (selection.Host != null)
            return selection.Host.Port;
        var announced = _tunnelRegistry.GetForSite(slug).FirstOrDefault(c => c.SpeedTestPort > 0);
        return announced?.SpeedTestPort ?? AgentOpenSpeedTestPort;
    }

    /// <summary>
    /// Resolves the current site's client-facing speed-test target. A per-site
    /// override (an IP/host or a full URL) wins over the selected agent's LAN
    /// IP - for agents whose reachable address isn't their detected LAN IP (e.g.
    /// behind a reverse proxy). An override that is a full URL is used as-is, so
    /// an operator can force http:// or a different host/port; a bare host/IP
    /// defaults to https on the agent port. Which agent hosts the test is
    /// <see cref="SiteSpeedTestHostSelector"/>'s call, shared with the Settings hint
    /// and path analysis.
    /// </summary>
    public async Task<Result> ResolveAsync()
    {
        // The main site normally has no agent to point clients at - this server IS the site's
        // speed test host. Once its agent covers the site, it is the host, exactly as on a
        // secondary site.
        if (_siteContext.IsDefault && !await _agentCoverage.CoversAsync(_siteContext.Slug))
            return new Result(null, null, null, UsesAgent: false, AgentOffline: false);

        var targetOverride = (await _settings.GetAsync(SystemSettingKeys.ClientSpeedTestTargetOverride))?.Trim();
        // Saves are validated; a value stored before that was is treated as no override at all.
        if (!NetworkOptimizer.Core.Helpers.UrlSafety.IsSafeHostOrHttpUrl(targetOverride))
            targetOverride = null;
        var selection = await _hostSelector.SelectAsync(_siteContext.Slug);
        var agentOffline = !selection.AgentReachable;

        // Agents reachable but none hosts a speed test (a gateway agent, or one that says it serves
        // none): nothing to point clients at unless the operator named a box. AnyOnGateway is what
        // the pages explain, so it only says gateway when one actually is.
        if (string.IsNullOrEmpty(targetOverride) && selection.AgentReachable && selection.Host == null)
        {
            return new Result(null, null, null, UsesAgent: false, AgentOffline: false, AgentOnGateway: selection.AnyOnGateway);
        }

        var effectiveTarget = !string.IsNullOrEmpty(targetOverride) ? targetOverride : selection.Host?.LanIp;
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
            // A bare host, IP, or host:port. A port the operator wrote wins over the agent's, and
            // an IPv6 literal needs brackets to sit in a URL at all.
            var (bareHost, bareHostPort) = SplitHostAndPort(effectiveTarget);
            baseUrl = $"https://{bareHost}:{bareHostPort ?? AgentSpeedTestPortFor(_siteContext.Slug, selection)}";
            host = bareHost.Trim('[', ']');
        }

        return new Result(effectiveTarget, baseUrl, host, UsesAgent: true, AgentOffline: agentOffline);
    }
}
