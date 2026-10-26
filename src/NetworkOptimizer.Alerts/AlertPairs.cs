namespace NetworkOptimizer.Alerts;

/// <summary>
/// Alert types that open a condition, and the event that closes each. The evaluators that publish
/// these keep their open state in memory; this map is what lets them, and anything reading alert
/// history, tell an open condition from a closed one.
/// </summary>
public static class AlertPairs
{
    /// <summary>The recovery event for each opening event type.</summary>
    public static readonly IReadOnlyDictionary<string, string> RecoveryFor = new Dictionary<string, string>
    {
        ["device.offline"] = "device.recovered",
        ["device.unmanaged"] = "device.managed",
        ["console.devices_unmanaged"] = "console.devices_managed",
        ["monitoring.target_offline"] = "monitoring.target_recovered",
        ["monitoring.wan_outage"] = "monitoring.wan_recovered",
        ["monitoring.wan_outage_partial"] = "monitoring.wan_recovered",
        ["monitoring.health_check_failed"] = "monitoring.health_check_recovered",
        ["wan.link_speed_degraded"] = "wan.link_speed_recovered",
        ["agent.offline"] = "agent.reconnected",
        ["console.connection_failed"] = "console.connection_restored",
        // One Starlink recovery closes each of these, naming the type in its context.
        ["starlink.dish_alert"] = "starlink.recovered",
        ["starlink.obstructed"] = "starlink.recovered",
        ["starlink.alignment_drift"] = "starlink.recovered",
        ["starlink.eth_speed_degraded"] = "starlink.recovered",
        ["starlink.outage_burst"] = "starlink.recovered",
        ["starlink.service_restricted"] = "starlink.recovered",
    };
}
