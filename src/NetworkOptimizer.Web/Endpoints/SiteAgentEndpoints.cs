using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Public endpoints for on-site agents: enrollment (one-time token to agent key
/// exchange) and heartbeats. Agents authenticate with their own credentials, so
/// these live under /api/public.
/// </summary>
public static class SiteAgentEndpoints
{
    private record EnrollmentRequest(string? Token, string? Version);
    private record HeartbeatRequest(string? AgentKey, string? Version);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/public/agents/enrollments", async (EnrollmentRequest request, AgentEnrollmentService enrollment) =>
        {
            var (success, agentKey, siteSlug, error) = await enrollment.EnrollAsync(request.Token ?? "", request.Version);
            return success
                ? Results.Ok(new { agentKey, siteSlug })
                : Results.BadRequest(new { error });
        });

        app.MapPost("/api/public/agents/heartbeats", async (HeartbeatRequest request, AgentEnrollmentService enrollment) =>
        {
            var ok = await enrollment.HeartbeatAsync(request.AgentKey ?? "", request.Version);
            return ok ? Results.NoContent() : Results.Unauthorized();
        });
    }
}
