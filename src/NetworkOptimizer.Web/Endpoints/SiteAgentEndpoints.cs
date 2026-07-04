using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Public endpoints for on-site agents: enrollment (one-time token to agent key
/// exchange) and heartbeats. Agents authenticate with their own credentials, so
/// these live under /api/public.
/// </summary>
public static class SiteAgentEndpoints
{
    private record EnrollmentRequest(string? Token, string? Version, string? LanIp);
    private record HeartbeatRequest(string? AgentKey, string? Version, string? LanIp);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/public/agents/enrollments", async (EnrollmentRequest request, AgentEnrollmentService enrollment, AgentTunnelOptions tunnel) =>
        {
            var (success, agentKey, siteSlug, error) = await enrollment.EnrollAsync(request.Token ?? "", request.Version, request.LanIp);
            return success
                ? Results.Ok(new { agentKey, siteSlug, tunnelPort = tunnel.Enabled ? tunnel.Port : (int?)null })
                : Results.BadRequest(new { error });
        });

        app.MapPost("/api/public/agents/heartbeats", async (HeartbeatRequest request, AgentEnrollmentService enrollment) =>
        {
            var ok = await enrollment.HeartbeatAsync(request.AgentKey ?? "", request.Version, request.LanIp);
            return ok ? Results.NoContent() : Results.Unauthorized();
        });
    }
}
