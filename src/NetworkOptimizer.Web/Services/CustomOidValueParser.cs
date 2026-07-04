using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Parses a raw SNMP custom-OID value into the Influx field type the user configured.
/// Shared by the directly-monitored medium tier and the agent-relayed path so both
/// store custom OIDs identically.
/// </summary>
public static class CustomOidValueParser
{
    public static object Parse(string raw, CustomOidValueType valueType) => valueType switch
    {
        CustomOidValueType.Integer => long.TryParse(raw, out var l) ? l : (object)raw,
        CustomOidValueType.Float => double.TryParse(raw, out var d) ? d : (object)raw,
        _ => raw
    };
}
