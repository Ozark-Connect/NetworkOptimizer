using NetworkOptimizer.Alerts.Delivery;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Endpoints;

public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this WebApplication app)
    {
        // --- Alert Rules ---
        app.MapGet("/api/alerts/rules", async (IAlertRepository repo) =>
            Results.Ok(await repo.GetRulesAsync()));

        app.MapPost("/api/alerts/rules", async (AlertRule rule, IAlertRepository repo) =>
        {
            var id = await repo.SaveRuleAsync(rule);
            return Results.Created($"/api/alerts/rules/{id}", rule);
        });

        app.MapPut("/api/alerts/rules/{id:int}", async (int id, AlertRule rule, IAlertRepository repo) =>
        {
            var existing = await repo.GetRuleAsync(id);
            if (existing == null) return Results.NotFound();

            existing.Name = rule.Name;
            existing.IsEnabled = rule.IsEnabled;
            existing.EventTypePattern = rule.EventTypePattern;
            existing.Source = rule.Source;
            existing.MinSeverity = rule.MinSeverity;
            existing.CooldownSeconds = rule.CooldownSeconds;
            existing.EscalationMinutes = rule.EscalationMinutes;
            existing.EscalationSeverity = rule.EscalationSeverity;
            existing.DigestOnly = rule.DigestOnly;
            existing.TargetDevices = rule.TargetDevices;

            await repo.UpdateRuleAsync(existing);
            return Results.Ok(existing);
        });

        app.MapDelete("/api/alerts/rules/{id:int}", async (int id, IAlertRepository repo) =>
        {
            await repo.DeleteRuleAsync(id);
            return Results.NoContent();
        });

        // --- Delivery Channels ---
        app.MapGet("/api/alerts/channels", async (IAlertRepository repo) =>
            Results.Ok(await repo.GetChannelsAsync()));

        app.MapPost("/api/alerts/channels", async (DeliveryChannel channel, IAlertRepository repo) =>
        {
            var id = await repo.SaveChannelAsync(channel);
            return Results.Created($"/api/alerts/channels/{id}", channel);
        });

        app.MapPut("/api/alerts/channels/{id:int}", async (int id, DeliveryChannel channel, IAlertRepository repo) =>
        {
            var existing = await repo.GetChannelAsync(id);
            if (existing == null) return Results.NotFound();

            existing.Name = channel.Name;
            existing.IsEnabled = channel.IsEnabled;
            existing.ChannelType = channel.ChannelType;
            existing.ConfigJson = channel.ConfigJson;
            existing.MinSeverity = channel.MinSeverity;
            existing.DigestEnabled = channel.DigestEnabled;
            existing.DigestSchedule = channel.DigestSchedule;

            await repo.UpdateChannelAsync(existing);
            return Results.Ok(existing);
        });

        app.MapDelete("/api/alerts/channels/{id:int}", async (int id, IAlertRepository repo) =>
        {
            await repo.DeleteChannelAsync(id);
            return Results.NoContent();
        });

        app.MapPost("/api/alerts/channels/{id:int}/test", async (int id, IAlertRepository repo, IEnumerable<IAlertDeliveryChannel> deliveryChannels) =>
        {
            var channel = await repo.GetChannelAsync(id);
            if (channel == null) return Results.NotFound();

            var handler = deliveryChannels.FirstOrDefault(d => d.ChannelType == channel.ChannelType);
            if (handler == null) return Results.BadRequest(new { error = $"No handler for channel type {channel.ChannelType}" });

            var (success, error) = await handler.TestAsync(channel);
            return Results.Ok(new { success, error });
        });

        // --- Alert History ---
        app.MapGet("/api/alerts", async (IAlertRepository repo, int limit = 100, string? source = null, AlertSeverity? minSeverity = null) =>
            Results.Ok(await repo.GetAlertHistoryAsync(limit, source, minSeverity)));

        app.MapGet("/api/alerts/active", async (IAlertRepository repo) =>
            Results.Ok(await repo.GetActiveAlertsAsync()));

        app.MapPut("/api/alerts/{id:int}/acknowledge", async (int id, IAlertRepository repo) =>
        {
            var alert = await repo.GetAlertAsync(id);
            if (alert == null) return Results.NotFound();

            alert.Status = AlertStatus.Acknowledged;
            alert.AcknowledgedAt = DateTime.UtcNow;
            await repo.UpdateAlertAsync(alert);
            return Results.Ok(alert);
        });

        app.MapPut("/api/alerts/{id:int}/resolve", async (int id, IAlertRepository repo) =>
        {
            var alert = await repo.GetAlertAsync(id);
            if (alert == null) return Results.NotFound();

            alert.Status = AlertStatus.Resolved;
            alert.ResolvedAt = DateTime.UtcNow;
            await repo.UpdateAlertAsync(alert);
            return Results.Ok(alert);
        });

        // --- Incidents ---
        app.MapGet("/api/alerts/incidents", async (IAlertRepository repo, int limit = 50) =>
            Results.Ok(await repo.GetIncidentsAsync(limit)));
    }
}
