using System.Text.Json;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Reads device health (CPU %, memory %, temperature, uptime) out of the UniFi console's
/// cached device data. Shared by the directly-monitored medium tier
/// (<see cref="MonitoringCollectionAgent"/>) and the agent-relayed SNMP path
/// (<see cref="AgentProbeResultSink"/>) so both fill SNMP-missing health fields identically.
/// </summary>
public static class UniFiDeviceHealthReader
{
    /// <summary>Health fields available from a UniFi device's cached stats. Any may be null.</summary>
    public readonly record struct ApiHealth(double? Cpu, double? MemPercent, double? TemperatureC, long? UptimeSeconds);

    /// <summary>
    /// Extracts CPU %, memory %, temperature, and uptime from a UniFi device's system-stats and
    /// temperature fields. Returns nulls for whatever the console didn't report.
    /// </summary>
    public static ApiHealth ExtractApiHealth(UniFiDeviceResponse device)
    {
        var ss = device.SystemStatsSimple;
        double? cpu = ss != null ? ParseJsonDouble(ss.Cpu) : null;
        double? mem = ss != null ? ParseJsonDouble(ss.Mem) : null;
        long? uptime = ss != null ? (long?)ParseJsonDouble(ss.Uptime) : null;
        double? temp = ParseDeviceTemperature(device);
        return new ApiHealth(cpu, mem, temp, uptime);
    }

    /// <summary>Parses a numeric UniFi stat that may arrive as a JSON number or string.</summary>
    public static double? ParseJsonDouble(JsonElement? el)
    {
        if (el == null || el.Value.ValueKind == JsonValueKind.Undefined) return null;
        if (el.Value.ValueKind == JsonValueKind.Number && el.Value.TryGetDouble(out var num)) return num;
        if (el.Value.ValueKind == JsonValueKind.String)
        {
            var s = el.Value.GetString()?.Trim();
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    /// <summary>
    /// Reads a device's temperature from the UniFi API, handling the switch (general_temperature /
    /// bare integer) and gateway (structured sensor array) shapes.
    /// </summary>
    public static double? ParseDeviceTemperature(UniFiDeviceResponse device)
    {
        // general_temperature: simple numeric field on switches (e.g., 72)
        if (device.GeneralTemperature.HasValue && device.GeneralTemperature.Value > 0)
            return device.GeneralTemperature.Value;

        // temperatures: structured array on gateways, bare integer on some devices
        if (device.Temperatures == null || device.Temperatures.Value.ValueKind == JsonValueKind.Undefined)
            return null;

        var el = device.Temperatures.Value;

        // Gateway: array of { name, type, value } - pick the CPU sensor
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var sensor in el.EnumerateArray())
            {
                var sType = sensor.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (string.Equals(sType, "cpu", StringComparison.OrdinalIgnoreCase))
                {
                    if (sensor.TryGetProperty("value", out var v) && v.TryGetDouble(out var tempVal))
                        return tempVal;
                }
            }
            // No CPU sensor found, take the first one
            foreach (var sensor in el.EnumerateArray())
            {
                if (sensor.TryGetProperty("value", out var v) && v.TryGetDouble(out var tempVal))
                    return tempVal;
            }
        }

        // Switch: bare integer
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var bare))
            return bare;

        return null;
    }
}
