namespace NetworkOptimizer.Web.Services.Monitoring.HealthChecks;

/// <summary>Alert event types a health check publishes.</summary>
public static class HealthCheckAlertTypes
{
    /// <summary>The check's condition held for its consecutive-sample count.</summary>
    public const string Failed = "monitoring.health_check_failed";

    /// <summary>A tripped check passed again.</summary>
    public const string Recovered = "monitoring.health_check_recovered";

    /// <summary>The check ran its remedy on the device.</summary>
    public const string Action = "monitoring.health_check_action";

    /// <summary>Influx <c>events</c> event_type tag for a remedy that ran.</summary>
    public const string InfluxEventType = "health_check";
}
