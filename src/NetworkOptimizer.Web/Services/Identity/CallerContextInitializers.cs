using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Populates the ambient <see cref="ICallerContext"/> for an HTTP request from the authenticated
/// principal plus request metadata (source IP, user-agent, correlation id). Runs after authentication
/// so <c>context.User</c> is resolved (design doc 06 - user caller, captured once per request).
/// </summary>
public sealed class CallerContextMiddleware
{
    private readonly RequestDelegate _next;

    public CallerContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ICallerContext caller)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            caller.SetUser(CallerInfo.ForUser(
                context.User,
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Headers.UserAgent.ToString(),
                context.TraceIdentifier));
        }

        await _next(context);
    }
}

/// <summary>
/// Populates the ambient <see cref="ICallerContext"/> once per Blazor circuit at initialization from
/// the authentication state (design doc 06 - <c>IHttpContextAccessor</c> is null/stale after the
/// WebSocket starts, so the user is captured here, not per call). Source IP / user-agent are not
/// available to a live circuit and are left null; audit records what it has.
/// </summary>
public sealed class CallerContextCircuitHandler : CircuitHandler
{
    private readonly ICallerContext _caller;
    private readonly AuthenticationStateProvider _authState;

    public CallerContextCircuitHandler(ICallerContext caller, AuthenticationStateProvider authState)
    {
        _caller = caller;
        _authState = authState;
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var state = await _authState.GetAuthenticationStateAsync();
        if (state.User.Identity?.IsAuthenticated == true)
            _caller.SetUser(CallerInfo.ForUser(state.User, sourceIp: null, userAgent: null, correlationId: circuit.Id));
    }
}
