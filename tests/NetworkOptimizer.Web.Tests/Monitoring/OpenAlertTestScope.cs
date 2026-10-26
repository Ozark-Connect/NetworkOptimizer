using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// A site's stored alerts, as an evaluator reads them back at start-up through OpenAlertSeed.
/// Times are relative to the real clock, because the seed's lookback is.
/// </summary>
internal static class OpenAlertTestScope
{
    private sealed class NoopSiteScope : IAlertSiteScope
    {
        public void UseSite(string? siteSlug) { }
    }

    /// <summary>A scope factory whose alert repository holds exactly these alerts.</summary>
    public static IServiceScopeFactory With(params AlertHistoryEntry[] alerts)
    {
        var repo = new Mock<IAlertRepository>();
        repo.Setup(r => r.GetUnresolvedAlertsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => alerts.Where(a => a.Status != AlertStatus.Resolved).ToList());
        repo.Setup(r => r.GetAlertsForDigestAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime since, CancellationToken _) => alerts.Where(a => a.TriggeredAt >= since).ToList());

        var services = new ServiceCollection();
        services.AddScoped<IAlertSiteScope, NoopSiteScope>();
        services.AddScoped(_ => repo.Object);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>A stored alert that fired <paramref name="ago"/> before now.</summary>
    public static AlertHistoryEntry Alert(string eventType, string? deviceId, TimeSpan ago,
        AlertSeverity severity = AlertSeverity.Warning, params (string Key, string Value)[] context) => new()
        {
            EventType = eventType,
            DeviceId = deviceId,
            Severity = severity,
            TriggeredAt = DateTime.UtcNow - ago,
            ContextJson = context.Length == 0 ? null : JsonSerializer.Serialize(context.ToDictionary(c => c.Key, c => c.Value)),
        };

    /// <summary>The same alert, resolved by a person.</summary>
    public static AlertHistoryEntry Resolved(this AlertHistoryEntry alert)
    {
        alert.Status = AlertStatus.Resolved;
        alert.ResolvedAt = DateTime.UtcNow;
        return alert;
    }
}
