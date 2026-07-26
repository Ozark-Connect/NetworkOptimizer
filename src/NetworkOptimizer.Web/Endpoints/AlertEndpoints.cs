using NetworkOptimizer.Alerts;
using NetworkOptimizer.Alerts.Delivery;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this WebApplication app)
    {
        // Gate 2 (design doc 06): every endpoint is mapped onto a group that carries its
        // authorization policy, which is what architecture test A1 checks. Reads are any
        // authenticated user; changes go through IAlertConfigService, which is gated and audited at
        // the service layer as well, so a live Blazor circuit cannot reach them either.
        var read = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);
        var admin = app.MapGroup("").RequireAuthorization(Policies.RequireAdmin);

        // --- Alert Rules ---
        read.MapGet("/api/alerts/rules", async (IAlertRepository repo) =>
            Results.Ok(await repo.GetRulesAsync()));

        admin.MapPost("/api/alerts/rules", async (AlertRule rule, IAlertConfigService config) =>
        {
            var id = await config.CreateRuleAsync(rule);
            return Results.Created($"/api/alerts/rules/{id}", rule);
        });

        admin.MapPut("/api/alerts/rules/{id:int}", async (int id, AlertRule rule, IAlertConfigService config) =>
        {
            var saved = await config.UpdateRuleAsync(id, rule);
            return saved == null ? Results.NotFound() : Results.Ok(saved);
        });

        admin.MapDelete("/api/alerts/rules/{id:int}", async (int id, IAlertConfigService config) =>
        {
            await config.DeleteRuleAsync(id);
            return Results.NoContent();
        });

        // --- Delivery Channels ---
        read.MapGet("/api/alerts/channels", async (IAlertRepository repo) =>
            Results.Ok(await repo.GetChannelsAsync()));

        admin.MapPost("/api/alerts/channels", async (DeliveryChannel channel, IAlertConfigService config) =>
        {
            var id = await config.CreateChannelAsync(channel);
            return Results.Created($"/api/alerts/channels/{id}", channel);
        });

        admin.MapPut("/api/alerts/channels/{id:int}", async (int id, DeliveryChannel channel, IAlertConfigService config) =>
        {
            var saved = await config.UpdateChannelAsync(id, channel);
            return saved == null ? Results.NotFound() : Results.Ok(saved);
        });

        admin.MapDelete("/api/alerts/channels/{id:int}", async (int id, IAlertConfigService config) =>
        {
            await config.DeleteChannelAsync(id);
            return Results.NoContent();
        });

        admin.MapPost("/api/alerts/channels/{id:int}/test", async (int id, IAlertRepository repo, IEnumerable<IAlertDeliveryChannel> deliveryChannels) =>
        {
            var channel = await repo.GetChannelAsync(id);
            if (channel == null) return Results.NotFound();

            var handler = deliveryChannels.FirstOrDefault(d => d.ChannelType == channel.ChannelType);
            if (handler == null) return Results.BadRequest(new { error = $"No handler for channel type {channel.ChannelType}" });

            var (success, error) = await handler.TestAsync(channel);
            return Results.Ok(new { success, error });
        });

        // --- Alert History ---
        read.MapGet("/api/alerts", async (IAlertRepository repo, int limit = 100, string? source = null, AlertSeverity? minSeverity = null) =>
            Results.Ok(await repo.GetAlertHistoryAsync(limit, source, minSeverity)));

        read.MapGet("/api/alerts/active", async (IAlertRepository repo) =>
            Results.Ok(await repo.GetActiveAlertsAsync()));

        admin.MapPut("/api/alerts/{id:int}/acknowledge", async (int id, IAlertConfigService config) =>
        {
            var alert = await config.AcknowledgeAlertAsync(id);
            return alert == null ? Results.NotFound() : Results.Ok(alert);
        });

        admin.MapPut("/api/alerts/{id:int}/resolve", async (int id, IAlertConfigService config) =>
        {
            var alert = await config.ResolveAlertAsync(id);
            return alert == null ? Results.NotFound() : Results.Ok(alert);
        });

        // --- Incidents ---
        read.MapGet("/api/alerts/incidents", async (IAlertRepository repo, int limit = 50) =>
            Results.Ok(await repo.GetIncidentsAsync(limit)));

        // --- Schedules ---
        read.MapGet("/api/alerts/schedules", async (IScheduleRepository repo) =>
            Results.Ok(await repo.GetAllAsync()));

        admin.MapPut("/api/alerts/schedules/{id:int}", async (int id, ScheduledTask updated, IAlertConfigService config) =>
        {
            var saved = await config.UpdateScheduleAsync(id, updated);
            return saved == null ? Results.NotFound() : Results.Ok(saved);
        });

        admin.MapPost("/api/alerts/schedules/{id:int}/run", async (int id, ScheduleService scheduleService, SiteContextService siteContext) =>
        {
            var started = await scheduleService.RunNowAsync(id, siteContext.Slug);
            return started ? Results.Ok(new { started = true }) : Results.Conflict(new { error = "Task is already running or not found" });
        });
    }
}
