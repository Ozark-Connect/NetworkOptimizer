namespace NetworkOptimizer.Storage.Services;

/// <summary>
/// Standard InfluxDB field names written by the monitoring pipeline. Custom OIDs that
/// match both an OID and its field name here are superseded by built-in polling and
/// removed automatically on first load.
/// </summary>
public static class InfluxFieldNames
{
    public const string FanSpeedRpm = "fan_speed_rpm";
}
